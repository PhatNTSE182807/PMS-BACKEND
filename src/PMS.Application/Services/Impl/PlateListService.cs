using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;

namespace PMS.Application.Services.Impl;

/// <summary>
/// Whitelist / Blacklist of a lot (FR-OPS-07, BR-16/17): Managers edit their lot's lists, Staff can read
/// them. Entries are never deleted (BR-21); removing one deactivates it.
/// </summary>
public class PlateListService(PmsDbContext pmsContext, LotScopeService lotScope, TimeProvider clock)
{
    public async Task<IReadOnlyList<PlateListEntryModel>> GetAsync(int lotId, string listType)
    {
        await lotScope.EnsureCanOperateAsync(lotId);
        var now = clock.GetUtcNow().UtcDateTime;
        var query = pmsContext.PlateLists.AsNoTracking()
            .Where(p => (p.ParkingLotId == lotId || p.ParkingLotId == null) && p.IsActive && p.ExpiresAt > now);
        if (!string.IsNullOrWhiteSpace(listType)) query = query.Where(p => p.ListType == listType);
        return (await query.OrderBy(p => p.ListType).ThenBy(p => p.PlateNormalized).ToListAsync()).Select(ToModel).ToList();
    }

    public async Task<PlateListEntryModel> AddAsync(int lotId, CreatePlateListEntryModel model)
    {
        await lotScope.EnsureCanManageAsync(lotId);
        var type = new[] { PlateListType.Whitelist, PlateListType.Blacklist }
            .FirstOrDefault(t => string.Equals(t, model.ListType?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new BadRequestException("ListType must be Whitelist or Blacklist.");
        var normalized = ParkingText.NormalizePlate(model.PlateNumber);
        if (normalized.Length < 4)
            throw new BadRequestException("Invalid plate.");
        if (string.IsNullOrWhiteSpace(model.Reason))
            throw new BadRequestException("A reason is required.");
        var now = clock.GetUtcNow().UtcDateTime;
        if (model.ExpiresAt.ToUniversalTime() <= now)
            throw new BadRequestException("The expiry date must be in the future.");

        var entry = new PlateListEntry
        {
            ListType = type,
            PlateNumber = model.PlateNumber.Trim().ToUpperInvariant(),
            PlateNormalized = normalized,
            ParkingLotId = lotId,
            Reason = model.Reason.Trim(),
            ExpiresAt = model.ExpiresAt.ToUniversalTime(),
            IsActive = true,
            CreatedBy = lotScope.UserId,
            CreatedAt = now
        };
        pmsContext.PlateLists.Add(entry);
        await pmsContext.SaveChangesAsync();
        return ToModel(entry);
    }

    public async Task<bool> DeactivateAsync(int lotId, int id)
    {
        await lotScope.EnsureCanManageAsync(lotId);
        var entry = await pmsContext.PlateLists.FirstOrDefaultAsync(p => p.Id == id && p.ParkingLotId == lotId)
                    ?? throw new NotFoundException("Entry not found.");
        entry.IsActive = false;
        await pmsContext.SaveChangesAsync();
        return true;
    }

    private static PlateListEntryModel ToModel(PlateListEntry p) => new()
    {
        Id = p.Id,
        ListType = p.ListType,
        PlateNumber = p.PlateNumber,
        ParkingLotId = p.ParkingLotId,
        Reason = p.Reason,
        ExpiresAt = DateTime.SpecifyKind(p.ExpiresAt, DateTimeKind.Utc),
        IsActive = p.IsActive,
        CreatedAt = DateTime.SpecifyKind(p.CreatedAt, DateTimeKind.Utc)
    };
}
