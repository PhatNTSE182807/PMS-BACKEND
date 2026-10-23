using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Slot holds and Zone Counters shared by reservations, the gates and the jobs. Changes are staged on
/// the context and the matching pushes are queued; call <see cref="FlushAsync"/> after SaveChanges.
/// Counter rule: a held (Reserved) or parked bay is not available; Occupied counts vehicles inside.
/// </summary>
public class ParkingInventory(PmsDbContext pmsContext, ILotRealtimeNotifier lotNotifier = null, IParkingRealtimeNotifier notifier = null)
{
    /// <summary>One gate per slot id: holds of the same slot never interleave inside this API process.</summary>
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> SlotGates = new();

    private readonly List<Func<Task>> _pushes = [];

    /// <summary>
    /// Runs <paramref name="action"/> while no other request of this process works on the same slots.
    /// Across processes the Slot RowVersion and the unique index IX_Reservations_SlotId_Held keep the
    /// first holder the winner (NFR-15); losers get a <see cref="ConflictException"/>.
    /// </summary>
    public static async Task<T> WithSlotLockAsync<T>(IEnumerable<int> slotIds, Func<Task<T>> action)
    {
        // Fixed order so two requests never wait on each other
        var gates = slotIds.Distinct().Order().Select(id => SlotGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1))).ToList();
        foreach (var gate in gates)
            await gate.WaitAsync();
        try
        {
            return await action();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("This slot was just taken by someone else.");
        }
        catch (DbUpdateException e) when (e.InnerException?.Message.Contains("IX_Reservations_SlotId_Held") == true)
        {
            throw new ConflictException("This slot was just taken by someone else.");
        }
        finally
        {
            foreach (var gate in gates)
                gate.Release();
        }
    }

    /// <summary>Vehicle family for slot / lane matching: all cars share bays; bicycles use motorbike floors.</summary>
    public static string Family(string vehicleTypeCode) =>
        vehicleTypeCode?.StartsWith("CAR", StringComparison.OrdinalIgnoreCase) == true ? "car"
        : vehicleTypeCode is "MOTORBIKE" or "BICYCLE" ? "two-wheel"
        : vehicleTypeCode ?? string.Empty;

    public async Task<Dictionary<int, VehicleType>> VehicleTypesAsync() =>
        await pmsContext.VehicleTypes.AsNoTracking().ToDictionaryAsync(v => v.Id);

    public static bool Compatible(IReadOnlyDictionary<int, VehicleType> types, int slotTypeId, int vehicleTypeId) =>
        slotTypeId == vehicleTypeId
        || (types.TryGetValue(slotTypeId, out var a) && types.TryGetValue(vehicleTypeId, out var b) && Family(a.Code) == Family(b.Code));

    /// <summary>Marks an Available slot as Reserved and takes one space off its zone counter.</summary>
    public async Task HoldAsync(Slot slot, int lotId)
    {
        if (!slot.IsActive || slot.Status != SlotStatus.Available)
            throw new ConflictException("This slot is no longer available.");

        slot.Status = SlotStatus.Reserved;
        await AdjustCounterAsync(slot.ZoneId, lotId, available: -1, occupied: 0);
        QueueSlot(lotId, slot);
    }

    /// <summary>Gives a held slot back (expired, cancelled, no-show, lot cancelled, slot change).</summary>
    public async Task ReleaseHoldAsync(int? slotId, int lotId)
    {
        if (slotId == null) return;
        var slot = await pmsContext.Slots.FirstOrDefaultAsync(s => s.Id == slotId);
        if (slot is not { Status: SlotStatus.Reserved }) return;

        slot.Status = SlotStatus.Available;
        await AdjustCounterAsync(slot.ZoneId, lotId, available: +1, occupied: 0);
        QueueSlot(lotId, slot);
    }

    /// <summary>
    /// The sensor confirmed the vehicle is in: its held slot (reservation) or chosen bay becomes Occupied;
    /// the counter loses a space only when none was held for it.
    /// </summary>
    public async Task ParkAsync(int? zoneId, int? slotId, int lotId)
    {
        var heldBefore = false;
        if (slotId != null)
        {
            var slot = await pmsContext.Slots.FirstOrDefaultAsync(s => s.Id == slotId);
            if (slot != null && slot.Status != SlotStatus.Occupied)
            {
                heldBefore = slot.Status == SlotStatus.Reserved;
                slot.Status = SlotStatus.Occupied;
                zoneId ??= slot.ZoneId;
                QueueSlot(lotId, slot);
            }
        }

        if (zoneId != null)
            await AdjustCounterAsync(zoneId.Value, lotId, available: heldBefore ? 0 : -1, occupied: +1);
    }

    /// <summary>The vehicle left (or a ghost session was closed): the bay and the space come back.</summary>
    public async Task LeaveAsync(int? zoneId, int? slotId, int lotId)
    {
        if (slotId != null)
        {
            var slot = await pmsContext.Slots.FirstOrDefaultAsync(s => s.Id == slotId);
            if (slot is { Status: SlotStatus.Occupied })
            {
                slot.Status = SlotStatus.Available;
                zoneId ??= slot.ZoneId;
                QueueSlot(lotId, slot);
            }
        }

        if (zoneId != null)
            await AdjustCounterAsync(zoneId.Value, lotId, available: +1, occupied: -1);
    }

    /// <summary>Moves a zone counter, kept within 0..capacity (BR-19: a forced entry may leave it off by one).</summary>
    public async Task AdjustCounterAsync(int zoneId, int lotId, int available, int occupied)
    {
        var counter = pmsContext.ZoneCounters.Local.FirstOrDefault(c => c.ZoneId == zoneId)
                      ?? await pmsContext.ZoneCounters.FirstOrDefaultAsync(c => c.ZoneId == zoneId);
        if (counter == null) return;

        counter.AvailableSpaces = Math.Clamp(counter.AvailableSpaces + available, 0, Math.Max(counter.TotalCapacity, 0));
        counter.OccupiedSpaces = Math.Clamp(counter.OccupiedSpaces + occupied, 0, Math.Max(counter.TotalCapacity, 0));
        counter.LastUpdatedAt = DateTime.UtcNow;

        var (a, o, t) = (counter.AvailableSpaces, counter.OccupiedSpaces, counter.TotalCapacity);
        if (notifier != null)
            _pushes.Add(() => notifier.ZoneCounterChangedAsync(lotId, zoneId, a, o, t));
    }

    /// <summary>Zones of the lot with their counter and the vehicle type of their bays.</summary>
    public async Task<List<ZoneCounterModel>> CountersAsync(int lotId)
    {
        var zones = await pmsContext.Zones.AsNoTracking()
            .Where(z => z.ParkingLotId == lotId && z.IsActive)
            .Select(z => new
            {
                z.Id,
                z.Code,
                z.Name,
                z.FloorNo,
                Counter = z.ZoneCounter,
                VehicleTypeId = z.Slots.Select(s => (int?)s.VehicleTypeId).FirstOrDefault()
            })
            .ToListAsync();

        return zones.OrderBy(z => z.FloorNo).ThenBy(z => z.Code).Select(z => new ZoneCounterModel
        {
            ZoneId = z.Id,
            ZoneCode = z.Code,
            ZoneName = z.Name,
            VehicleTypeId = z.VehicleTypeId,
            TotalCapacity = z.Counter?.TotalCapacity ?? 0,
            OccupiedSpaces = z.Counter?.OccupiedSpaces ?? 0,
            AvailableSpaces = z.Counter?.AvailableSpaces ?? 0
        }).ToList();
    }

    /// <summary>Queues a push to run after SaveChanges.</summary>
    public void Queue(Func<Task> push) => _pushes.Add(push);

    /// <summary>Sends the queued pushes (after a successful save). Failures never break the request.</summary>
    public async Task FlushAsync()
    {
        var pushes = _pushes.ToList();
        _pushes.Clear();
        foreach (var push in pushes)
        {
            try
            {
                await push();
            }
            catch
            {
                // Real-time is best effort; clients poll as a fallback
            }
        }
    }

    private void QueueSlot(int lotId, Slot slot)
    {
        var (id, status) = (slot.Id, slot.Status);
        if (lotNotifier != null)
            _pushes.Add(() => lotNotifier.SlotStatusChangedAsync(lotId, id, status));
    }
}
