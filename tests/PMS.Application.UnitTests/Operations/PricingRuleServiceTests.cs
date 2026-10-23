using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PMS.Application.Exceptions;
using PMS.Application.Models.Pricing;
using PMS.Application.Models.Validators.Pricing;
using PMS.Application.Services.Impl;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

public class PricingRuleServiceTests
{
    private const string ManagerUserId = "manager-1";
    private const string OtherUserId = "manager-2";

    private async Task<(OperationsTestContext ctx, PricingRuleService service)> CreateTestSetupAsync()
    {
        var t = new OperationsTestContext();
        await t.SeedAsync();

        // Assign manager to lot
        t.Lot.ManagerUserId = ManagerUserId;
        await t.Db.SaveChangesAsync();

        t.ActAs(ManagerUserId, "Manager");
        var scope = new LotScopeService(t.Db, t.Claims);
        var tariffs = new TariffProvider(t.Db, t.Options);
        var service = new PricingRuleService(t.Db, scope, tariffs, t.Options, t.Clock);

        return (t, service);
    }

    private static PricingRule BuildRule(
        int lotId,
        int vehicleTypeId,
        string code,
        string name,
        string status = "Active",
        int priority = 0,
        DateTime? effectiveFrom = null) => new()
    {
        Code = code,
        Name = name,
        ParkingLotId = lotId,
        VehicleTypeId = vehicleTypeId,
        ChargeModel = "Block",
        BlockMinutes = 60,
        BlockPrice = 25_000,
        GraceMinutes = 15,
        Currency = "VND",
        Priority = priority,
        Status = status,
        VersionNo = 1,
        EffectiveFrom = effectiveFrom ?? DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task GetPricingRulesAsync_ReturnsRules_WhenManagerAuthorized()
    {
        var (t, service) = await CreateTestSetupAsync();
        var carType = await t.Db.VehicleTypes.FirstAsync(v => v.Code == "CAR_4");

        var rule1 = BuildRule(t.Lot.Id, carType.Id, "TEST-CAR-1", "Car Tariff Active", status: "Active", priority: 10, effectiveFrom: t.Now);
        var rule2 = BuildRule(t.Lot.Id, carType.Id, "TEST-CAR-2", "Car Tariff Draft", status: "Draft", priority: 5, effectiveFrom: t.Now);

        t.Db.PricingRules.AddRange(rule1, rule2);
        await t.Db.SaveChangesAsync();

        var allRules = await service.GetPricingRulesAsync(t.Lot.Id);
        allRules.Should().HaveCount(2);
        allRules[0].Priority.Should().Be(10);
        allRules[0].VehicleTypeCode.Should().Be("CAR_4");

        var activeOnly = await service.GetPricingRulesAsync(t.Lot.Id, "Active");
        activeOnly.Should().HaveCount(1);
        activeOnly[0].Status.Should().Be("Active");
    }

    [Fact]
    public async Task GetPricingRulesAsync_ThrowsForbidden_WhenUserDoesNotManageLot()
    {
        var (t, service) = await CreateTestSetupAsync();
        t.ActAs(OtherUserId, "Manager");

        var act = async () => await service.GetPricingRulesAsync(t.Lot.Id);

        await act.Should().ThrowAsync<ForbiddenException>()
            .WithMessage("*You do not manage this parking lot*");
    }

    [Fact]
    public async Task GetPricingRuleByIdAsync_ReturnsRule_WhenExists()
    {
        var (t, service) = await CreateTestSetupAsync();
        var carType = await t.Db.VehicleTypes.FirstAsync(v => v.Code == "CAR_4");

        var rule = BuildRule(t.Lot.Id, carType.Id, "TEST-RULE-BYID", "Single Rule", effectiveFrom: t.Now);
        rule.BlockPrice = 30_000;
        t.Db.PricingRules.Add(rule);
        await t.Db.SaveChangesAsync();

        var result = await service.GetPricingRuleByIdAsync(t.Lot.Id, rule.Id);

        result.Should().NotBeNull();
        result.Id.Should().Be(rule.Id);
        result.Code.Should().Be("TEST-RULE-BYID");
        result.VehicleTypeCode.Should().Be("CAR_4");
    }

    [Fact]
    public async Task GetPricingRuleByIdAsync_ThrowsNotFound_WhenMissing()
    {
        var (t, service) = await CreateTestSetupAsync();

        var act = async () => await service.GetPricingRuleByIdAsync(t.Lot.Id, 99999);

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*Pricing rule with ID 99999 not found*");
    }

    [Fact]
    public async Task CreatePricingRuleAsync_CreatesRuleAndHistory_WhenValid()
    {
        var (t, service) = await CreateTestSetupAsync();
        var carType = await t.Db.VehicleTypes.FirstAsync(v => v.Code == "CAR_4");

        var model = new CreatePricingRuleModel
        {
            Name = "New Car Tariff",
            VehicleTypeId = carType.Id,
            ChargeModel = "Block",
            FirstBlockMinutes = 120,
            FirstBlockPrice = 35_000,
            BlockMinutes = 60,
            BlockPrice = 25_000,
            GraceMinutes = 15,
            MaxDailyFee = 350_000,
            OvernightSurcharge = 120_000,
            LostTicketFee = 150_000,
            Status = "Active"
        };

        var created = await service.CreatePricingRuleAsync(t.Lot.Id, model);

        created.Should().NotBeNull();
        created.Id.Should().BeGreaterThan(0);
        created.VersionNo.Should().Be(1);
        created.Status.Should().Be("Active");
        created.ApprovedBy.Should().Be(ManagerUserId);
        created.VehicleTypeCode.Should().Be("CAR_4");

        // Verify history record
        var histories = await t.Db.PricingRuleHistories.Where(h => h.PricingRuleId == created.Id).ToListAsync();
        histories.Should().HaveCount(1);
        histories[0].ChangeType.Should().Be("Create");
        histories[0].ChangedBy.Should().Be(ManagerUserId);
    }

    [Fact]
    public async Task CreatePricingRuleAsync_ThrowsBadRequest_WhenVehicleTypeDoesNotExist()
    {
        var (t, service) = await CreateTestSetupAsync();

        var model = new CreatePricingRuleModel
        {
            Name = "Invalid Vehicle Rule",
            VehicleTypeId = 9999,
            ChargeModel = "Block"
        };

        var act = async () => await service.CreatePricingRuleAsync(t.Lot.Id, model);

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*Vehicle type with ID 9999 does not exist*");
    }

    [Fact]
    public async Task CreatePricingRuleAsync_ThrowsConflict_WhenCodeAlreadyExists()
    {
        var (t, service) = await CreateTestSetupAsync();
        var carType = await t.Db.VehicleTypes.FirstAsync(v => v.Code == "CAR_4");

        var rule = BuildRule(t.Lot.Id, carType.Id, "EXISTING-CODE", "Existing Rule", effectiveFrom: t.Now);
        t.Db.PricingRules.Add(rule);
        await t.Db.SaveChangesAsync();

        var model = new CreatePricingRuleModel
        {
            Code = "existing-code",
            Name = "Duplicate Code Rule",
            VehicleTypeId = carType.Id,
            ChargeModel = "Block"
        };

        var act = async () => await service.CreatePricingRuleAsync(t.Lot.Id, model);

        await act.Should().ThrowAsync<ConflictException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public async Task UpdatePricingRuleAsync_IncrementsVersion_AndRecordsHistory()
    {
        var (t, service) = await CreateTestSetupAsync();
        var carType = await t.Db.VehicleTypes.FirstAsync(v => v.Code == "CAR_4");

        var rule = BuildRule(t.Lot.Id, carType.Id, "UPDATE-TEST", "Initial Rule", status: "Draft", effectiveFrom: t.Now);
        rule.BlockPrice = 20_000;
        t.Db.PricingRules.Add(rule);
        await t.Db.SaveChangesAsync();

        var updateModel = new UpdatePricingRuleModel
        {
            Name = "Updated Name",
            VehicleTypeId = carType.Id,
            ChargeModel = "Block",
            BlockMinutes = 60,
            BlockPrice = 25_000,
            EffectiveFrom = t.Now,
            Status = "Draft",
            Reason = "Price increase per hour"
        };

        var updated = await service.UpdatePricingRuleAsync(t.Lot.Id, rule.Id, updateModel);

        updated.VersionNo.Should().Be(2);
        updated.BlockPrice.Should().Be(25_000);
        updated.Name.Should().Be("Updated Name");

        var histories = await t.Db.PricingRuleHistories
            .Where(h => h.PricingRuleId == rule.Id)
            .OrderByDescending(h => h.Id)
            .ToListAsync();
        histories.Should().HaveCount(1);
        histories[0].ChangeType.Should().Be("Update");
        histories[0].Reason.Should().Be("Price increase per hour");
    }

    [Fact]
    public async Task SetPricingRuleStatusAsync_UpdatesStatus_AndApprovesWhenActive()
    {
        var (t, service) = await CreateTestSetupAsync();
        var carType = await t.Db.VehicleTypes.FirstAsync(v => v.Code == "CAR_4");

        var rule = BuildRule(t.Lot.Id, carType.Id, "STATUS-TEST", "Draft Rule", status: "Draft", effectiveFrom: t.Now);
        t.Db.PricingRules.Add(rule);
        await t.Db.SaveChangesAsync();

        var result = await service.SetPricingRuleStatusAsync(t.Lot.Id, rule.Id, new SetPricingRuleStatusModel
        {
            Status = "Active",
            Reason = "Approved by manager"
        });

        result.Status.Should().Be("Active");
        result.ApprovedBy.Should().Be(ManagerUserId);
        result.ApprovedAt.Should().NotBeNull();

        var histories = await t.Db.PricingRuleHistories.Where(h => h.PricingRuleId == rule.Id).ToListAsync();
        histories.Should().HaveCount(1);
        histories[0].ChangeType.Should().Be("StatusChange");
    }

    [Fact]
    public async Task DeletePricingRuleAsync_ArchivesRule_AndRecordsHistory()
    {
        var (t, service) = await CreateTestSetupAsync();
        var carType = await t.Db.VehicleTypes.FirstAsync(v => v.Code == "CAR_4");

        var rule = BuildRule(t.Lot.Id, carType.Id, "DELETE-TEST", "Rule to Delete", status: "Active", effectiveFrom: t.Now);
        t.Db.PricingRules.Add(rule);
        await t.Db.SaveChangesAsync();

        await service.DeletePricingRuleAsync(t.Lot.Id, rule.Id);

        var archived = await t.Db.PricingRules.FirstAsync(r => r.Id == rule.Id);
        archived.Status.Should().Be("Archived");

        var histories = await t.Db.PricingRuleHistories.Where(h => h.PricingRuleId == rule.Id).ToListAsync();
        histories.Should().HaveCount(1);
        histories[0].ChangeType.Should().Be("Delete");
    }

    [Fact]
    public async Task SimulatePricingAsync_CalculatesSimulation_Correctly()
    {
        var (t, service) = await CreateTestSetupAsync();
        var carType = await t.Db.VehicleTypes.FirstAsync(v => v.Code == "CAR_4");

        // Rule: 15m grace, first 2h 35k, each next 1h 25k, lost ticket 150k
        var rule = BuildRule(t.Lot.Id, carType.Id, "SIM-RULE", "Simulation Rule", status: "Active", effectiveFrom: t.Now.AddDays(-1));
        rule.GraceMinutes = 15;
        rule.FirstBlockMinutes = 120;
        rule.FirstBlockPrice = 35_000;
        rule.BlockMinutes = 60;
        rule.BlockPrice = 25_000;
        rule.MaxDailyFee = 350_000;
        rule.LostTicketFee = 150_000;

        t.Db.PricingRules.Add(rule);
        await t.Db.SaveChangesAsync();

        // Stay of 7 hours 15 minutes:
        // First block (2h): 35,000
        // Next 5h 15m (6 blocks of 1h): 6 * 25,000 = 150,000
        // Gross total = 185,000
        // Pre-booking deposit = 50,000
        // Amount due = 185,000 - 50,000 = 135,000 VND
        var entry = new DateTime(2026, 10, 23, 8, 30, 0, DateTimeKind.Utc);
        var exit = new DateTime(2026, 10, 23, 15, 45, 0, DateTimeKind.Utc);

        var simResult = await service.SimulatePricingAsync(t.Lot.Id, new PricingSimulationRequestModel
        {
            RuleId = rule.Id,
            EntryTime = entry,
            ExitTime = exit,
            DepositAmount = 50_000,
            IsLostTicket = false
        });

        simResult.DurationMinutes.Should().Be(435); // 7h 15m
        simResult.WithinGrace.Should().BeFalse();
        simResult.GrossFee.Should().Be(185_000);
        simResult.DepositApplied.Should().Be(50_000);
        simResult.DepositRefund.Should().Be(0);
        simResult.AmountDue.Should().Be(135_000);
        simResult.LostTicketFee.Should().Be(0);
        simResult.Days.Should().HaveCount(1);
    }

    [Fact]
    public void CreatePricingRuleModelValidator_Validates_Correctly()
    {
        var validator = new CreatePricingRuleModelValidator();

        var invalidModel = new CreatePricingRuleModel
        {
            Name = "", // empty name
            VehicleTypeId = 0, // invalid type
            ChargeModel = "InvalidModel",
            EntryFee = -100, // negative fee
            GraceMinutes = -5,
            BlockMinutes = -10
        };

        var result = validator.Validate(invalidModel);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Name");
        result.Errors.Should().Contain(e => e.PropertyName == "VehicleTypeId");
        result.Errors.Should().Contain(e => e.PropertyName == "ChargeModel");
        result.Errors.Should().Contain(e => e.PropertyName == "EntryFee");
        result.Errors.Should().Contain(e => e.PropertyName == "GraceMinutes");
        result.Errors.Should().Contain(e => e.PropertyName == "BlockMinutes");
    }
}
