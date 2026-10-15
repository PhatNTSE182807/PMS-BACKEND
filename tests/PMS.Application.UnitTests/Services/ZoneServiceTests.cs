using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.Zone;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Services;

public class ZoneServiceTests
{
    private readonly PmsDbContext _context;
    private readonly ZoneService _sut;
    private readonly ParkingLot _testLot;

    public ZoneServiceTests()
    {
        var options = new DbContextOptionsBuilder<PmsDbContext>()
            .UseInMemoryDatabase(databaseName: $"ZoneTests_{Guid.NewGuid()}")
            .Options;
        _context = new PmsDbContext(options);
        _sut = new ZoneService(_context);

        _testLot = new ParkingLot
        {
            Code = "LOT-TEST",
            Name = "Test Lot",
            Address = "123 Street",
            Status = "Active",
            IsDeleted = false
        };
        _context.ParkingLots.Add(_testLot);
        _context.SaveChanges();
    }

    [Fact]
    public async Task CreateAsync_WhenValid_Should_Create_Zone()
    {
        var model = new CreateZoneModel
        {
            ParkingLotId = _testLot.Id,
            Code = "ZONE-A",
            Name = "Floor 1 - Area A",
            ZoneType = "Floor",
            FloorNo = 1,
            Capacity = 50
        };

        var result = await _sut.CreateAsync(model);

        result.Should().NotBeNull();
        result.Id.Should().BeGreaterThan(0);
        result.Code.Should().Be("ZONE-A");
        result.Name.Should().Be("Floor 1 - Area A");
        result.IsActive.Should().BeTrue();
        result.ParkingLotId.Should().Be(_testLot.Id);

        var inDb = await _context.Zones.FindAsync(result.Id);
        inDb.Should().NotBeNull();
        inDb.Code.Should().Be("ZONE-A");
    }

    [Fact]
    public async Task CreateAsync_WhenParentLotNotFound_Should_Throw_NotFound()
    {
        var model = new CreateZoneModel
        {
            ParkingLotId = 9999,
            Code = "ZONE-X",
            Name = "Orphan Zone",
            Capacity = 20
        };

        var act = async () => await _sut.CreateAsync(model);

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*Parking lot with ID 9999 was not found*");
    }

    [Fact]
    public async Task CreateAsync_WhenDuplicateCodeInSameLot_Should_Throw_BadRequest()
    {
        _context.Zones.Add(new Zone
        {
            ParkingLotId = _testLot.Id,
            Code = "ZONE-DUP",
            Name = "Existing Zone",
            IsActive = true
        });
        await _context.SaveChangesAsync();

        var model = new CreateZoneModel
        {
            ParkingLotId = _testLot.Id,
            Code = "ZONE-DUP",
            Name = "Another Zone",
            Capacity = 30
        };

        var act = async () => await _sut.CreateAsync(model);

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*already exists in this parking lot*");
    }

    [Fact]
    public async Task DeleteAsync_WhenNoOccupiedSlots_Should_Deactivate_Zone()
    {
        var zone = new Zone
        {
            ParkingLotId = _testLot.Id,
            Code = "ZONE-DEL",
            Name = "Zone to delete",
            IsActive = true
        };
        _context.Zones.Add(zone);
        await _context.SaveChangesAsync();

        var result = await _sut.DeleteAsync(zone.Id);

        result.Should().BeTrue();
        var deleted = await _context.Zones.FindAsync(zone.Id);
        deleted.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_WhenZoneHasOccupiedSlots_Should_Throw_BadRequest()
    {
        var zone = new Zone
        {
            ParkingLotId = _testLot.Id,
            Code = "ZONE-OCC",
            Name = "Occupied Zone",
            IsActive = true
        };
        _context.Zones.Add(zone);
        await _context.SaveChangesAsync();

        _context.Slots.Add(new Slot
        {
            ZoneId = zone.Id,
            VehicleTypeId = 1,
            Code = "A-01",
            Status = "Occupied",
            IsActive = true,
            RowVersion = Array.Empty<byte>()
        });
        await _context.SaveChangesAsync();

        var act = async () => await _sut.DeleteAsync(zone.Id);

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*contains occupied slots*");
    }

    [Fact]
    public async Task CreateAsync_Should_Create_ZoneCounter_And_Set_MaxReservationSlots()
    {
        var model = new CreateZoneModel
        {
            ParkingLotId = _testLot.Id,
            Code = "ZONE-CNT",
            Name = "Zone With Counter",
            Capacity = 40,
            MaxReservationSlots = 10
        };

        var result = await _sut.CreateAsync(model);

        result.Should().NotBeNull();
        result.MaxReservationSlots.Should().Be(10);

        var counter = await _context.ZoneCounters.FirstOrDefaultAsync(zc => zc.ZoneId == result.Id);
        counter.Should().NotBeNull();
        counter.TotalCapacity.Should().Be(40);
        counter.AvailableSpaces.Should().Be(40);
        counter.OccupiedSpaces.Should().Be(0);
    }

    [Fact]
    public async Task UpdateAsync_Should_Update_MaxReservationSlots_And_Sync_ZoneCounter()
    {
        var createModel = new CreateZoneModel
        {
            ParkingLotId = _testLot.Id,
            Code = "ZONE-UPD",
            Name = "Zone to Update",
            Capacity = 50,
            MaxReservationSlots = 15
        };
        var created = await _sut.CreateAsync(createModel);

        var updateModel = new UpdateZoneModel
        {
            Name = "Zone Updated",
            Capacity = 60,
            MaxReservationSlots = 20
        };

        var result = await _sut.UpdateAsync(created.Id, updateModel);

        result.MaxReservationSlots.Should().Be(20);
        result.Capacity.Should().Be(60);

        var counter = await _context.ZoneCounters.FirstOrDefaultAsync(zc => zc.ZoneId == created.Id);
        counter.Should().NotBeNull();
        counter.TotalCapacity.Should().Be(60);
        counter.AvailableSpaces.Should().Be(60);
    }
}
