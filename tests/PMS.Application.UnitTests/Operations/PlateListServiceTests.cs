using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using PMS.Application.Exceptions;
using PMS.Application.Models.Operations;
using PMS.Application.Services.Impl;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>A lot's whitelist and blacklist: Managers edit them, Staff read them.</summary>
public class PlateListServiceTests
{
    private static async Task<(OperationsTestContext T, PlateListService Service)> CreateAsync()
    {
        var t = await new OperationsTestContext().SeedAsync();
        t.ActAs(t.ManagerUserId, "Manager");
        return (t, new PlateListService(t.Db, new LotScopeService(t.Db, t.Claims), t.Clock));
    }

    private static CreatePlateListEntryModel Entry(OperationsTestContext t, string type = "whitelist", string plate = " 51a-999.99 ") => new()
    {
        ListType = type,
        PlateNumber = plate,
        Reason = "  VIP  ",
        ExpiresAt = t.Now.AddDays(30)
    };

    [Fact]
    public async Task AddAsync_Should_Store_A_Normalised_Entry()
    {
        var (t, service) = await CreateAsync();

        var entry = await service.AddAsync(t.Lot.Id, Entry(t));

        entry.ListType.Should().Be(PlateListType.Whitelist);
        entry.PlateNumber.Should().Be("51A-999.99");
        entry.Reason.Should().Be("VIP");
        entry.IsActive.Should().BeTrue();
        entry.ExpiresAt.Kind.Should().Be(DateTimeKind.Utc);
        t.Db.PlateLists.Single().CreatedBy.Should().Be(t.ManagerUserId);
    }

    [Fact]
    public async Task AddAsync_Should_Reject_Bad_Type_Plate_Reason_Or_Expiry()
    {
        var (t, service) = await CreateAsync();

        await FluentActions.Invoking(() => service.AddAsync(t.Lot.Id, Entry(t, type: "Greylist")))
            .Should().ThrowAsync<BadRequestException>().WithMessage("ListType must be Whitelist or Blacklist.");
        await FluentActions.Invoking(() => service.AddAsync(t.Lot.Id, Entry(t, plate: "1-2")))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Invalid plate.");

        var noReason = Entry(t);
        noReason.Reason = " ";
        await FluentActions.Invoking(() => service.AddAsync(t.Lot.Id, noReason))
            .Should().ThrowAsync<BadRequestException>().WithMessage("A reason is required.");

        var expired = Entry(t);
        expired.ExpiresAt = t.Now.AddMinutes(-1);
        await FluentActions.Invoking(() => service.AddAsync(t.Lot.Id, expired))
            .Should().ThrowAsync<BadRequestException>().WithMessage("The expiry date must be in the future.");
    }

    [Fact]
    public async Task AddAsync_Should_Refuse_Staff()
    {
        var (t, service) = await CreateAsync();
        t.ActAs(t.StaffUserId, "Staff");

        await FluentActions.Invoking(() => service.AddAsync(t.Lot.Id, Entry(t))).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task GetAsync_Should_List_Active_Unexpired_Entries_For_Staff()
    {
        var (t, service) = await CreateAsync();
        await service.AddAsync(t.Lot.Id, Entry(t, "Blacklist", "30E-111.11"));
        await service.AddAsync(t.Lot.Id, Entry(t, "Whitelist", "51A-999.99"));
        var removed = await service.AddAsync(t.Lot.Id, Entry(t, "Whitelist", "51A-888.88"));
        await service.DeactivateAsync(t.Lot.Id, removed.Id);
        var expiring = await service.AddAsync(t.Lot.Id, Entry(t, "Whitelist", "51A-777.77"));
        t.Db.PlateLists.Single(p => p.Id == expiring.Id).ExpiresAt = t.Now.AddSeconds(-1);
        await t.Db.SaveChangesAsync();

        t.ActAs(t.StaffUserId, "Staff");
        var all = await service.GetAsync(t.Lot.Id, null);
        var whitelist = await service.GetAsync(t.Lot.Id, PlateListType.Whitelist);

        all.Select(e => e.ListType).Should().Equal(PlateListType.Blacklist, PlateListType.Whitelist);
        whitelist.Should().ContainSingle(e => e.PlateNumber == "51A-999.99");
    }

    [Fact]
    public async Task DeactivateAsync_Should_Throw_For_An_Unknown_Entry()
    {
        var (t, service) = await CreateAsync();

        await FluentActions.Invoking(() => service.DeactivateAsync(t.Lot.Id, 404)).Should().ThrowAsync<NotFoundException>();
    }
}
