using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Helpers;
using PMS.Application.Models.Layout;
using PMS.Application.Models.ParkingLot;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Services;

/// <summary>Lots built from a ready-made model, and the 3D / 2D layout read from them.</summary>
public class LayoutServiceTests
{
    private readonly PmsDbContext _context;
    private readonly ParkingLotService _lots;
    private readonly LayoutService _sut;

    public LayoutServiceTests()
    {
        var options = new DbContextOptionsBuilder<PmsDbContext>()
            .UseInMemoryDatabase($"LayoutTests_{Guid.NewGuid()}")
            .Options;
        _context = new PmsDbContext(options);
        _context.VehicleTypes.AddRange(
            new VehicleType { Code = "CAR_4", Name = "4-Seat Car", IsActive = true },
            new VehicleType { Code = "MOTORBIKE", Name = "Motorbike", IsActive = true });
        _context.SaveChanges();

        _lots = new ParkingLotService(_context);
        _sut = new LayoutService(_context);
    }

    private Task<ParkingLotResponseModel> CreateLotAsync(string template = "L3S20") =>
        _lots.CreateAsync(new CreateParkingLotModel
        {
            Name = "Template Lot",
            Address = "1 Test Street",
            Is24Hours = true,
            LayoutTemplate = template
        }, "admin-id");

    [Fact]
    public void Templates_Should_Match_The_Frontend_Models()
    {
        _sut.GetTemplates().Select(t => t.Id).Should().Equal("L3S20", "L3S50", "L4S30");

        var tower = LotTemplates.Find("l4s30");
        tower.TotalSlots.Should().Be(120);
        tower.CarSlots.Should().Be(90);
        tower.MotorbikeSlots.Should().Be(30);
        tower.Floors.Select(f => f.Kind).Should().Equal("motorbike", "car", "car", "car");
        tower.Floors.Should().OnlyContain(f => f.Rows * f.Cols >= tower.SlotsPerFloor);
    }

    [Fact]
    public async Task CreateLot_Should_Build_Zones_Slots_And_A_Published_Layout_From_The_Model()
    {
        var lot = await CreateLotAsync("L3S50");

        lot.Capacity.Should().Be(150);
        lot.LayoutTemplate.Should().Be("L3S50");

        var zones = await _context.Zones.Include(z => z.Slots).Include(z => z.ZoneCounter)
            .Where(z => z.ParkingLotId == lot.Id).OrderBy(z => z.FloorNo).ToListAsync();
        zones.Select(z => z.Code).Should().Equal("L1", "L2", "L3");
        zones.Should().OnlyContain(z => z.Slots.Count == 50 && z.ZoneCounter.TotalCapacity == 50);
        zones[0].Slots.First().Code.Should().Be("L1-01");

        var layout = await _sut.GetPublishedAsync(lot.Id);
        layout.TemplateId.Should().Be("L3S50");
        layout.Version.Should().Be(1);
        layout.Floors.Should().HaveCount(3).And.OnlyContain(f => f.Rows == 4 && f.Cols == 13);
        layout.Slots.Should().HaveCount(150);
        layout.Slots.Select(s => (s.FloorNo, s.Row, s.Col)).Should().OnlyHaveUniqueItems();
        layout.Slots.Where(s => s.FloorNo == 0).Should().OnlyContain(s => s.VehicleTypeCode == "MOTORBIKE");
        layout.Slots.Where(s => s.FloorNo > 0).Should().OnlyContain(s => s.VehicleTypeCode == "CAR_4");
    }

    [Fact]
    public async Task ApplyTemplate_Should_Refuse_A_Lot_That_Already_Has_Zones()
    {
        var lot = await CreateLotAsync();

        var act = () => _lots.ApplyTemplateAsync(lot.Id, "L4S30", "admin-id");

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task ApplyTemplate_Should_Build_An_Older_Lot_That_Has_No_Zones()
    {
        var legacy = new ParkingLot { Code = "OLD-1", Name = "Old lot", Address = "x", Status = "Active", TimeZoneId = "SE Asia Standard Time", CreatedAt = DateTime.UtcNow };
        _context.ParkingLots.Add(legacy);
        await _context.SaveChangesAsync();

        var result = await _lots.ApplyTemplateAsync(legacy.Id, "L3S20", "admin-id");

        result.TotalZones.Should().Be(3);
        result.TotalSlots.Should().Be(60);
        (await _sut.GetPublishedAsync(legacy.Id)).Slots.Should().HaveCount(60);
    }

    [Fact]
    public async Task ApplyTemplate_Should_Reject_An_Unknown_Model()
    {
        var legacy = new ParkingLot { Code = "OLD-2", Name = "Old lot", Address = "x", Status = "Active", TimeZoneId = "SE Asia Standard Time", CreatedAt = DateTime.UtcNow };
        _context.ParkingLots.Add(legacy);
        await _context.SaveChangesAsync();

        var act = () => _lots.ApplyTemplateAsync(legacy.Id, "CUSTOM", "admin-id");

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task GetStatus_Should_Return_Slot_Statuses_And_Zone_Free_Counts()
    {
        var lot = await CreateLotAsync();
        var slot = await _context.Slots.FirstAsync(s => s.Code == "L2-01");
        slot.Status = "Occupied";
        var counter = await _context.ZoneCounters.FirstAsync(c => c.ZoneId == slot.ZoneId);
        counter.AvailableSpaces = 19;
        await _context.SaveChangesAsync();

        var status = await _sut.GetStatusAsync(lot.Id);

        status.Slots.Should().HaveCount(60);
        status.Slots.Single(s => s.SlotId == slot.Id).Status.Should().Be("Occupied");
        status.Zones.Single(z => z.ZoneId == slot.ZoneId).Free.Should().Be(19);
    }

    [Fact]
    public async Task Get2D_Should_Return_Cells_Or_Zone_Blocks_By_Detail()
    {
        var lot = await CreateLotAsync();
        var slot = await _context.Slots.FirstAsync(s => s.Code == "L1-01");
        slot.Status = "Occupied";
        await _context.SaveChangesAsync();

        var cells = await _sut.Get2DAsync(lot.Id, LayoutDetail.Slots);
        cells.Detail.Should().Be("slots");
        cells.Floors.Should().HaveCount(3);
        cells.Floors[0].Cells.Should().HaveCount(20);
        cells.Floors[0].Free.Should().Be(19);
        cells.Floors[0].Cells.First().Should().BeEquivalentTo(new { Row = 0, Col = 0, Code = "L1-01", Status = "Occupied" });

        var blocks = await _sut.Get2DAsync(lot.Id, "ZONES");
        blocks.Detail.Should().Be("zones");
        blocks.Floors[0].Cells.Should().BeEmpty();
        blocks.Floors[0].ZoneBlocks.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Code = "L1", MinRow = 0, MaxRow = 1, MinCol = 0, MaxCol = 9, Total = 20, Free = 19 });
    }

    [Fact]
    public async Task Layout_Of_A_Lot_Without_A_Model_Should_Be_NotFound()
    {
        var legacy = new ParkingLot { Code = "OLD-3", Name = "Old lot", Address = "x", Status = "Active", TimeZoneId = "SE Asia Standard Time", CreatedAt = DateTime.UtcNow };
        _context.ParkingLots.Add(legacy);
        await _context.SaveChangesAsync();

        var act = () => _sut.GetPublishedAsync(legacy.Id);

        await act.Should().ThrowAsync<NotFoundException>();
    }
}
