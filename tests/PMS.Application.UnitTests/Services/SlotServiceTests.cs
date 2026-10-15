using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.Slot;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Services;

public class SlotServiceTests
{
    private readonly PmsDbContext _context;
    private readonly SlotService _sut;
    private readonly Zone _testZone;
    private readonly VehicleType _testVehicleType;

    public SlotServiceTests()
    {
        var options = new DbContextOptionsBuilder<PmsDbContext>()
            .UseInMemoryDatabase(databaseName: $"SlotTests_{Guid.NewGuid()}")
            .Options;
        _context = new PmsDbContext(options);
        _sut = new SlotService(_context);

        var lot = new ParkingLot
        {
            Code = "LOT-SLOT-TEST",
            Name = "Lot for slot tests",
            Address = "123 Test St",
            Status = "Active",
            IsDeleted = false
        };
        _context.ParkingLots.Add(lot);

        _testZone = new Zone
        {
            ParkingLot = lot,
            Code = "ZONE-S1",
            Name = "Zone S1",
            IsActive = true
        };
        _context.Zones.Add(_testZone);

        _testVehicleType = new VehicleType
        {
            Code = "CAR_4",
            Name = "4-Seat Car",
            IsActive = true
        };
        _context.VehicleTypes.Add(_testVehicleType);

        _context.SaveChanges();
    }

    [Fact]
    public async Task CreateAsync_WhenValid_Should_Create_Slot()
    {
        var model = new CreateSlotModel
        {
            ZoneId = _testZone.Id,
            VehicleTypeId = _testVehicleType.Id,
            Code = "SLOT-01",
            Status = "Available",
            PosX = 10.5m,
            PosY = 20.0m
        };

        var result = await _sut.CreateAsync(model);

        result.Should().NotBeNull();
        result.Id.Should().BeGreaterThan(0);
        result.Code.Should().Be("SLOT-01");
        result.Status.Should().Be("Available");
        result.IsActive.Should().BeTrue();
        result.ZoneId.Should().Be(_testZone.Id);
        result.VehicleTypeId.Should().Be(_testVehicleType.Id);

        var inDb = await _context.Slots.FindAsync(result.Id);
        inDb.Should().NotBeNull();
        inDb.Code.Should().Be("SLOT-01");
    }

    [Fact]
    public async Task CreateAsync_WhenZoneNotFound_Should_Throw_NotFound()
    {
        var model = new CreateSlotModel
        {
            ZoneId = 9999,
            VehicleTypeId = _testVehicleType.Id,
            Code = "SLOT-02",
            Status = "Available"
        };

        var act = async () => await _sut.CreateAsync(model);

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*Zone with ID 9999 was not found*");
    }

    [Fact]
    public async Task CreateAsync_WhenVehicleTypeNotFound_Should_Throw_NotFound()
    {
        var model = new CreateSlotModel
        {
            ZoneId = _testZone.Id,
            VehicleTypeId = 8888,
            Code = "SLOT-03",
            Status = "Available"
        };

        var act = async () => await _sut.CreateAsync(model);

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*Vehicle type with ID 8888 was not found*");
    }

    [Fact]
    public async Task CreateAsync_WhenDuplicateCodeInSameZone_Should_Throw_BadRequest()
    {
        _context.Slots.Add(new Slot
        {
            ZoneId = _testZone.Id,
            VehicleTypeId = _testVehicleType.Id,
            Code = "SLOT-DUP",
            Status = "Available",
            IsActive = true,
            RowVersion = Array.Empty<byte>()
        });
        await _context.SaveChangesAsync();

        var model = new CreateSlotModel
        {
            ZoneId = _testZone.Id,
            VehicleTypeId = _testVehicleType.Id,
            Code = "SLOT-DUP",
            Status = "Available"
        };

        var act = async () => await _sut.CreateAsync(model);

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*already exists in this zone*");
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenValid_Should_Update_Status()
    {
        var slot = new Slot
        {
            ZoneId = _testZone.Id,
            VehicleTypeId = _testVehicleType.Id,
            Code = "SLOT-ST",
            Status = "Available",
            IsActive = true,
            RowVersion = Array.Empty<byte>()
        };
        _context.Slots.Add(slot);
        await _context.SaveChangesAsync();

        var result = await _sut.UpdateStatusAsync(slot.Id, "Occupied");

        result.Should().BeTrue();
        var updated = await _context.Slots.FindAsync(slot.Id);
        updated.Status.Should().Be("Occupied");
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenInvalidStatus_Should_Throw_BadRequest()
    {
        var slot = new Slot
        {
            ZoneId = _testZone.Id,
            VehicleTypeId = _testVehicleType.Id,
            Code = "SLOT-BADST",
            Status = "Available",
            IsActive = true,
            RowVersion = Array.Empty<byte>()
        };
        _context.Slots.Add(slot);
        await _context.SaveChangesAsync();

        var act = async () => await _sut.UpdateStatusAsync(slot.Id, "InvalidStatusXYZ");

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*Allowed values: Available, Occupied, Reserved, Maintenance*");
    }

    [Fact]
    public async Task DeleteAsync_WhenSlotIsOccupied_Should_Throw_BadRequest()
    {
        var slot = new Slot
        {
            ZoneId = _testZone.Id,
            VehicleTypeId = _testVehicleType.Id,
            Code = "SLOT-OCC",
            Status = "Occupied",
            IsActive = true,
            RowVersion = Array.Empty<byte>()
        };
        _context.Slots.Add(slot);
        await _context.SaveChangesAsync();

        var act = async () => await _sut.DeleteAsync(slot.Id);

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*currently occupied*");
    }

    [Fact]
    public async Task DeleteAsync_WhenSlotNotOccupied_Should_Deactivate()
    {
        var slot = new Slot
        {
            ZoneId = _testZone.Id,
            VehicleTypeId = _testVehicleType.Id,
            Code = "SLOT-DEL",
            Status = "Available",
            IsActive = true,
            RowVersion = Array.Empty<byte>()
        };
        _context.Slots.Add(slot);
        await _context.SaveChangesAsync();

        var result = await _sut.DeleteAsync(slot.Id);

        result.Should().BeTrue();
        var deleted = await _context.Slots.FindAsync(slot.Id);
        deleted.IsActive.Should().BeFalse();
    }
}
