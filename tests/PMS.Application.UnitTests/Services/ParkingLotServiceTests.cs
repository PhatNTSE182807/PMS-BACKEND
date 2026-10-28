using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.ParkingLot;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Services;

public class ParkingLotServiceTests
{
    private readonly PmsDbContext _context;
    private readonly ParkingLotService _sut;

    public ParkingLotServiceTests()
    {
        var options = new DbContextOptionsBuilder<PmsDbContext>()
            .UseInMemoryDatabase(databaseName: $"ParkingLotTests_{Guid.NewGuid()}")
            .Options;
        _context = new PmsDbContext(options);
        // Parking lot models build their slots for these vehicle types
        _context.VehicleTypes.AddRange(
            new VehicleType { Code = "CAR_4", Name = "4-Seat Car", IsActive = true },
            new VehicleType { Code = "MOTORBIKE", Name = "Motorbike", IsActive = true });
        _context.SaveChanges();
        _sut = new ParkingLotService(_context);
    }

    [Fact]
    public async Task CreateAsync_WhenValid_Should_Create_Lot()
    {
        var model = new CreateParkingLotModel
        {
            Code = "LOT-Q1-01",
            Name = "District 1 Central Parking",
            Address = "123 Le Loi, Ben Nghe, District 1",
            City = "Ho Chi Minh City",
            District = "District 1",
            LayoutTemplate = "L3S20",
            Is24Hours = true
        };

        var result = await _sut.CreateAsync(model, "admin-user-id");

        result.Should().NotBeNull();
        result.Id.Should().BeGreaterThan(0);
        result.Code.Should().Be("LOT-Q1-01");
        result.Name.Should().Be("District 1 Central Parking");
        // A new lot has no Manager and Staff yet: it is saved as Active but shown as Maintenance
        result.StoredStatus.Should().Be("Active");
        result.Status.Should().Be("Maintenance");
        result.IsStaffed.Should().BeFalse();
        result.IsBookable.Should().BeFalse();
        result.IsDeleted.Should().BeFalse();
        result.CreatedBy.Should().Be("admin-user-id");

        var inDb = await _context.ParkingLots.FindAsync(result.Id);
        inDb.Should().NotBeNull();
        inDb.Code.Should().Be("LOT-Q1-01");
    }

    [Fact]
    public async Task CreateAsync_WhenDuplicateCode_Should_Throw_BadRequest()
    {
        _context.ParkingLots.Add(new ParkingLot
        {
            Code = "LOT-DUP",
            Name = "Existing Lot",
            Address = "456 Test",
            Status = "Active",
            IsDeleted = false
        });
        await _context.SaveChangesAsync();

        var model = new CreateParkingLotModel
        {
            Code = "LOT-DUP",
            Name = "Another Lot",
            Address = "789 Test",
            LayoutTemplate = "L3S20",
        };

        var act = async () => await _sut.CreateAsync(model);

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public async Task GetByIdAsync_WhenExists_Should_Return_Lot()
    {
        var lot = new ParkingLot
        {
            Code = "LOT-02",
            Name = "Lot Two",
            Address = "Address 2",
            Status = "Active",
            IsDeleted = false
        };
        _context.ParkingLots.Add(lot);
        await _context.SaveChangesAsync();

        var result = await _sut.GetByIdAsync(lot.Id);

        result.Should().NotBeNull();
        result.Code.Should().Be("LOT-02");
    }

    [Fact]
    public async Task GetByIdAsync_WhenNotFound_Should_Throw_NotFound()
    {
        var act = async () => await _sut.GetByIdAsync(9999);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task GetPublicLotsAsync_Should_Show_Open_And_Maintenance_Lots_But_Not_Closed_Or_Deleted_Ones()
    {
        _context.ParkingLots.AddRange(
            new ParkingLot { Code = "L1", Name = "Active Lot", Address = "A1", Status = "Active", IsDeleted = false },
            new ParkingLot { Code = "L2", Name = "Maintenance Lot", Address = "A2", Status = "Maintenance", IsDeleted = false },
            new ParkingLot { Code = "L3", Name = "Deleted Lot", Address = "A3", Status = "Active", IsDeleted = true },
            new ParkingLot { Code = "L4", Name = "Closed Lot", Address = "A4", Status = "TemporarilyClosed", IsDeleted = false }
        );
        await _context.SaveChangesAsync();

        var result = await _sut.GetPublicLotsAsync();

        result.Select(l => l.Code).Should().BeEquivalentTo(["L1", "L2"]);
        result.Should().OnlyContain(l => !l.IsBookable && l.Status == "Maintenance");
    }

    [Fact]
    public async Task A_Lot_Is_Bookable_Only_With_An_Active_Manager_And_Active_Staff()
    {
        var lot = new ParkingLot { Code = "L1", Name = "Lot", Address = "A1", Status = "Active", ManagerUserId = "manager-1" };
        _context.ParkingLots.Add(lot);
        _context.Employees.Add(new Employee { UserId = "manager-1", EmployeeCode = "QL1", FullName = "Manager", Position = "Manager", Status = "Active" });
        await _context.SaveChangesAsync();

        // Manager only: still under maintenance
        (await _sut.GetByIdAsync(lot.Id)).Status.Should().Be("Maintenance");

        var staff = new Employee { UserId = "staff-1", EmployeeCode = "NV1", FullName = "Staff", Position = "Staff", Status = "Active", ParkingLotId = lot.Id };
        _context.Employees.Add(staff);
        await _context.SaveChangesAsync();

        var staffed = await _sut.GetByIdAsync(lot.Id);
        staffed.Status.Should().Be("Active");
        staffed.IsBookable.Should().BeTrue();
        (await _sut.GetPublicLotsAsync()).Single().IsBookable.Should().BeTrue();

        // The only Staff member is deactivated: back to maintenance
        staff.Status = "Inactive";
        await _context.SaveChangesAsync();
        var unstaffed = (await _sut.GetPublicLotsAsync()).Single();
        unstaffed.Status.Should().Be("Maintenance");
        unstaffed.IsBookable.Should().BeFalse();
        unstaffed.MaintenanceNotice.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task UpdateStatusAsync_Should_Update_Status_And_Reason()
    {
        var lot = new ParkingLot
        {
            Code = "LOT-STAT",
            Name = "Status Test Lot",
            Address = "Address",
            Status = "Active",
            IsDeleted = false
        };
        _context.ParkingLots.Add(lot);
        await _context.SaveChangesAsync();

        var model = new UpdateParkingLotStatusModel
        {
            Status = "Maintenance",
            ClosureReason = "Floor repaving",
            MaintenanceNotice = "Closed Oct 15-18"
        };

        var result = await _sut.UpdateStatusAsync(lot.Id, model);

        result.Should().BeTrue();
        var updated = await _context.ParkingLots.FindAsync(lot.Id);
        updated.Status.Should().Be("Maintenance");
        updated.ClosureReason.Should().Be("Floor repaving");
        updated.MaintenanceNotice.Should().Be("Closed Oct 15-18");
    }

    [Fact]
    public async Task DeleteAsync_WhenNoActiveSessions_Should_SoftDelete()
    {
        var lot = new ParkingLot
        {
            Code = "LOT-DEL",
            Name = "Delete Test Lot",
            Address = "Address",
            Status = "Active",
            IsDeleted = false
        };
        _context.ParkingLots.Add(lot);
        await _context.SaveChangesAsync();

        var result = await _sut.DeleteAsync(lot.Id);

        result.Should().BeTrue();
        var deleted = await _context.ParkingLots.FindAsync(lot.Id);
        deleted.IsDeleted.Should().BeTrue();
        deleted.Status.Should().Be("Inactive");
    }

    [Fact]
    public async Task DeleteAsync_WhenHasActiveSessions_Should_Throw_BadRequest()
    {
        var lot = new ParkingLot
        {
            Code = "LOT-SESS",
            Name = "Session Lot",
            Address = "Address",
            Status = "Active",
            IsDeleted = false
        };
        _context.ParkingLots.Add(lot);
        await _context.SaveChangesAsync();

        _context.ParkingSessions.Add(new ParkingSession
        {
            ParkingLotId = lot.Id,
            SessionCode = "SESS-01",
            Status = "Active",
            EntryPlateNumber = "51A-12345",
            EntryPlateNormalized = "51A12345",
            EntryTime = DateTime.UtcNow,
            RowVersion = Array.Empty<byte>()
        });
        await _context.SaveChangesAsync();

        var act = async () => await _sut.DeleteAsync(lot.Id);

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*active parking sessions*");
    }
}
