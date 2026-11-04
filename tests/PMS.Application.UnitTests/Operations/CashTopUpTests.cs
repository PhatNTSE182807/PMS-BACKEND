using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using PMS.Application.Exceptions;
using PMS.Application.Models.Operations;
using PMS.Application.Services;
using PMS.Application.Services.Impl;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>A Staff drawer short of banknotes: the Staff asks, the lot's Manager approves, the notes join the drawer.</summary>
public class CashTopUpTests
{
    private static async Task<(OperationsTestContext T, CashDrawerService Drawers)> CreateAsync()
    {
        var t = await new OperationsTestContext().SeedAsync();
        return (t, new CashDrawerService(t.Db, new LotScopeService(t.Db, t.Claims), t.Options, t.Clock));
    }

    private static RequestCashTopUpModel Ask(Dictionary<int, int> notes, string reason = null) => new() { Notes = notes, Reason = reason };

    private static void AsStaff(OperationsTestContext t) => t.ActAs(t.StaffUserId, "Staff");

    private static void AsManager(OperationsTestContext t) => t.ActAs(t.ManagerUserId, "Manager");

    [Fact]
    public async Task Approved_Request_Should_Add_The_Notes_To_The_Drawer()
    {
        var (t, drawers) = await CreateAsync();
        AsStaff(t);
        await drawers.OpenAsync(t.Lot.Id, new OpenCashDrawerModel { Float = new() { [50_000] = 2 } });

        var request = await drawers.RequestTopUpAsync(t.Lot.Id, Ask(new() { [10_000] = 10, [5_000] = 4 }, "Out of small notes for change"));
        request.Status.Should().Be(CashTopUpStatus.Pending);
        request.Total.Should().Be(120_000);
        request.StaffName.Should().Be("Staff");
        request.DrawerOpen.Should().BeTrue();

        // Waiting: nothing is counted yet, but the Staff sees the request on the drawer
        var waiting = await drawers.GetCurrentAsync(t.Lot.Id);
        waiting.ExpectedTotal.Should().Be(100_000);
        waiting.TopUps.Should().ContainSingle(r => r.Id == request.Id && r.Status == CashTopUpStatus.Pending);

        AsManager(t);
        (await drawers.GetTopUpsForLotAsync(t.Lot.Id, null)).Should().ContainSingle(r => r.Id == request.Id);
        var approved = await drawers.ApproveTopUpAsync(request.Id, new DecideCashTopUpModel());
        approved.Status.Should().Be(CashTopUpStatus.Approved);
        approved.DecidedByName.Should().Be("Manager");
        (await drawers.GetTopUpsForLotAsync(t.Lot.Id, null)).Should().BeEmpty();

        AsStaff(t);
        var drawer = await drawers.GetCurrentAsync(t.Lot.Id);
        drawer.TopUpTotal.Should().Be(120_000);
        drawer.ExpectedTotal.Should().Be(220_000);
        drawer.ExpectedNotes.Should().BeEquivalentTo(new Dictionary<int, int> { [50_000] = 2, [10_000] = 10, [5_000] = 4 });

        // Counting exactly what is now in the drawer balances it
        var closed = await drawers.CloseAsync(t.Lot.Id, new CloseCashDrawerModel { Counted = new() { [50_000] = 2, [10_000] = 10, [5_000] = 4 } });
        closed.Difference.Should().Be(0);
        closed.Status.Should().Be(CashCloseStatus.Closed);
    }

    [Fact]
    public async Task Request_Should_Open_A_Drawer_When_None_Is_Open()
    {
        var (t, drawers) = await CreateAsync();
        AsStaff(t);

        var request = await drawers.RequestTopUpAsync(t.Lot.Id, Ask(new() { [20_000] = 5 }));

        var drawer = await drawers.GetCurrentAsync(t.Lot.Id);
        drawer.Status.Should().Be(CashCloseStatus.Open);
        drawer.Id.Should().Be(request.DrawerId);
    }

    [Fact]
    public async Task Request_Should_Need_Known_Banknotes()
    {
        var (t, drawers) = await CreateAsync();
        AsStaff(t);

        await FluentActions.Invoking(() => drawers.RequestTopUpAsync(t.Lot.Id, Ask(new())))
            .Should().ThrowAsync<BadRequestException>().WithMessage("List the banknotes*");
        await FluentActions.Invoking(() => drawers.RequestTopUpAsync(t.Lot.Id, Ask(new() { [3_000] = 1 })))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*not a banknote*");
    }

    [Fact]
    public async Task A_Drawer_Should_Have_One_Waiting_Request_At_A_Time()
    {
        var (t, drawers) = await CreateAsync();
        AsStaff(t);
        var first = await drawers.RequestTopUpAsync(t.Lot.Id, Ask(new() { [10_000] = 10 }));

        await FluentActions.Invoking(() => drawers.RequestTopUpAsync(t.Lot.Id, Ask(new() { [5_000] = 10 })))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*already have a request waiting*");

        (await drawers.CancelTopUpAsync(t.Lot.Id, first.Id)).Status.Should().Be(CashTopUpStatus.Cancelled);
        (await drawers.RequestTopUpAsync(t.Lot.Id, Ask(new() { [5_000] = 10 }))).Status.Should().Be(CashTopUpStatus.Pending);

        AsManager(t);
        await FluentActions.Invoking(() => drawers.ApproveTopUpAsync(first.Id, new DecideCashTopUpModel()))
            .Should().ThrowAsync<BadRequestException>().WithMessage("The Staff cancelled*");
    }

    [Fact]
    public async Task Rejecting_Should_Need_A_Reason_And_Add_Nothing()
    {
        var (t, drawers) = await CreateAsync();
        AsStaff(t);
        await drawers.OpenAsync(t.Lot.Id, new OpenCashDrawerModel { Float = new() { [50_000] = 2 } });
        var request = await drawers.RequestTopUpAsync(t.Lot.Id, Ask(new() { [10_000] = 10 }));

        AsManager(t);
        await FluentActions.Invoking(() => drawers.RejectTopUpAsync(request.Id, new DecideCashTopUpModel { Note = " " }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Give the Staff a reason*");
        var rejected = await drawers.RejectTopUpAsync(request.Id, new DecideCashTopUpModel { Note = "Use the 50,000 notes first." });
        rejected.Status.Should().Be(CashTopUpStatus.Rejected);
        rejected.DecisionNote.Should().Be("Use the 50,000 notes first.");

        AsStaff(t);
        var drawer = await drawers.GetCurrentAsync(t.Lot.Id);
        drawer.TopUpTotal.Should().Be(0);
        drawer.ExpectedTotal.Should().Be(100_000);
        await FluentActions.Invoking(() => drawers.CancelTopUpAsync(t.Lot.Id, request.Id))
            .Should().ThrowAsync<BadRequestException>().WithMessage("The Manager already decided*");
    }

    [Fact]
    public async Task Approving_Should_Need_The_Drawer_Still_Open()
    {
        var (t, drawers) = await CreateAsync();
        AsStaff(t);
        var request = await drawers.RequestTopUpAsync(t.Lot.Id, Ask(new() { [10_000] = 10 }));
        await drawers.CloseAsync(t.Lot.Id, new CloseCashDrawerModel { Counted = new() });

        AsManager(t);
        await FluentActions.Invoking(() => drawers.ApproveTopUpAsync(request.Id, new DecideCashTopUpModel()))
            .Should().ThrowAsync<BadRequestException>().WithMessage("The Staff already closed this drawer*");
    }

    [Fact]
    public async Task Only_The_Lots_Manager_Should_Decide()
    {
        var (t, drawers) = await CreateAsync();
        AsStaff(t);
        var request = await drawers.RequestTopUpAsync(t.Lot.Id, Ask(new() { [10_000] = 10 }));

        // The Staff cannot approve their own request
        await FluentActions.Invoking(() => drawers.ApproveTopUpAsync(request.Id, new DecideCashTopUpModel()))
            .Should().ThrowAsync<Exception>();
        await FluentActions.Invoking(() => drawers.GetTopUpsForLotAsync(t.Lot.Id, null))
            .Should().ThrowAsync<Exception>();

        // A Manager of another lot neither
        t.ActAs("manager-2", "Manager");
        await FluentActions.Invoking(() => drawers.ApproveTopUpAsync(request.Id, new DecideCashTopUpModel()))
            .Should().ThrowAsync<Exception>();
        t.Db.CashTopUpRequests.Single(r => r.Id == request.Id).Status.Should().Be(CashTopUpStatus.Pending);
    }
}
