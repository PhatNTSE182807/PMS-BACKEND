using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using PMS.Application.Exceptions;
using PMS.Application.Models.Operations;
using PMS.Application.Services;
using PMS.DataAccess.Persistence.Entities;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>
/// Lost ticket / card (FR-OPS-05, SRS 4.2.4): conditional approval (BR-13), Manager approval with
/// Maker-Checker (BR-14), and a rejected vehicle staying in the lot.
/// </summary>
public class LostTicketFlowTests
{
    private const string Plate = "51G-555.66";
    private const string AdminUserId = "admin-1";

    /// <summary>Default CAR_4 tariff (no pricing rule seeded).</summary>
    private const decimal CarLostTicketFee = 100_000;

    /// <summary>A walk-in car parked for 90 minutes; the Staff is acting.</summary>
    private static async Task<long> ParkedCarAsync(OperationsTestContext t)
    {
        var result = await t.Gates.CheckInAsync(t.Lot.Id, new CheckInModel { PlateNumber = Plate, VehicleTypeId = t.TypeId("CAR_4") });
        result.Outcome.Should().Be("Created");
        await t.Gates.ConfirmEntryAsync(result.Session.Id);
        t.Clock.Advance(TimeSpan.FromMinutes(90));
        return result.Session.Id;
    }

    private static SessionViolation[] Cases(OperationsTestContext t, long sessionId) =>
        t.Db.SessionViolations.Where(v => v.SessionId == sessionId && v.ViolationType == ViolationType.LostTicket)
            .OrderBy(v => v.Id).ToArray();

    private static LostTicketModel Report(bool conditionsMet = false) =>
        new() { Reason = "Paper ticket lost; registration and ID checked", ConditionsMet = conditionsMet };

    [Fact]
    public async Task A_Manager_Approval_Sends_The_Session_To_Payment_With_The_Fine()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var id = await ParkedCarAsync(t);

        var reported = await t.Gates.ReportLostTicketAsync(id, Report());

        reported.Status.Should().Be(SessionStatus.LostTicketPending);
        reported.LostTicketStatus.Should().Be(LostTicketCaseStatus.Pending);
        reported.LostTicketReportedBy.Should().Be(t.StaffUserId);
        reported.LostTicketReason.Should().Be("Paper ticket lost; registration and ID checked");

        t.ActAs(t.ManagerUserId, "Manager");
        var decided = await t.Gates.DecideLostTicketAsync(id, true, "Papers match the entry photo");

        decided.Status.Should().Be(SessionStatus.PendingPayment);
        decided.LostTicketStatus.Should().Be(LostTicketCaseStatus.Approved);
        decided.LostTicketDecisionNote.Should().Be("Papers match the entry photo");
        var lostCase = Cases(t, id).Single();
        lostCase.ApprovalMode.Should().Be(LostTicketApproval.Manager);
        lostCase.DecidedBy.Should().Be(t.ManagerUserId);
        lostCase.DecidedAt.Should().NotBeNull();
        lostCase.IsResolved.Should().BeTrue();

        t.ActAs(t.StaffUserId, "Staff");
        var quote = await t.Gates.RequestPaymentAsync(id, new CheckoutQuoteRequestModel());
        quote.LostTicketFee.Should().Be(CarLostTicketFee);
        quote.AmountDue.Should().Be(quote.Fee + CarLostTicketFee);
    }

    [Fact]
    public async Task A_Rejected_Vehicle_Stays_In_The_Lot_Until_A_New_Case_Is_Approved()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var id = await ParkedCarAsync(t);
        await t.Gates.ReportLostTicketAsync(id, Report());

        t.ActAs(t.ManagerUserId, "Manager");
        var rejected = await t.Gates.DecideLostTicketAsync(id, false, "Registration does not match the driver");

        rejected.Status.Should().Be(SessionStatus.Active);
        rejected.LostTicketStatus.Should().Be(LostTicketCaseStatus.Rejected);

        // No payment point and no exit lane takes the fee while the rejection stands
        t.ActAs(t.StaffUserId, "Staff");
        var quote = () => t.Gates.RequestPaymentAsync(id, new CheckoutQuoteRequestModel());
        var cash = () => t.Gates.PayCashAsync(id, new CashPaymentModel { Amount = 1_000_000 });
        var exit = () => t.Gates.CheckoutAsync(id, new CheckoutModel { PlateNumber = Plate });
        await quote.Should().ThrowAsync<BadRequestException>().WithMessage("*was rejected*");
        await cash.Should().ThrowAsync<BadRequestException>().WithMessage("*was rejected*");
        await exit.Should().ThrowAsync<BadRequestException>().WithMessage("*was rejected*");
        t.Db.ParkingSessions.Single(s => s.Id == id).Status.Should().Be(SessionStatus.Active);

        // The owner comes back with full papers: a new case, approved this time
        await t.Gates.ReportLostTicketAsync(id, Report());
        t.ActAs(t.ManagerUserId, "Manager");
        await t.Gates.DecideLostTicketAsync(id, true, null);

        t.ActAs(t.StaffUserId, "Staff");
        var due = await t.Gates.RequestPaymentAsync(id, new CheckoutQuoteRequestModel());
        due.LostTicketFee.Should().Be(CarLostTicketFee, "the fine is charged once, however many cases were opened");
        await t.Gates.PayCashAsync(id, new CashPaymentModel { Amount = due.AmountDue });
        var left = await t.Gates.CheckoutAsync(id, new CheckoutModel { PlateNumber = Plate });

        left.BarrierOpen.Should().BeTrue();
        left.Session.Status.Should().Be(SessionStatus.Completed);
        Cases(t, id).Select(c => c.Status).Should().Equal(LostTicketCaseStatus.Rejected, LostTicketCaseStatus.Approved);
    }

    [Fact]
    public async Task The_Person_Who_Reported_The_Case_Cannot_Decide_It()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var id = await ParkedCarAsync(t);

        t.ActAs(t.ManagerUserId, "Manager");
        await t.Gates.ReportLostTicketAsync(id, Report());
        var approveOwn = () => t.Gates.DecideLostTicketAsync(id, true, null);
        var rejectOwn = () => t.Gates.DecideLostTicketAsync(id, false, "Not the owner");

        await approveOwn.Should().ThrowAsync<ForbiddenException>().WithMessage("*another Manager or an Admin*");
        await rejectOwn.Should().ThrowAsync<ForbiddenException>();
        Cases(t, id).Single().Status.Should().Be(LostTicketCaseStatus.Pending);

        t.ActAs(AdminUserId, "Admin");
        var decided = await t.Gates.DecideLostTicketAsync(id, true, "Checked by Admin");

        decided.Status.Should().Be(SessionStatus.PendingPayment);
        Cases(t, id).Single().DecidedBy.Should().Be(AdminUserId);
    }

    [Fact]
    public async Task A_Conditional_Approval_Goes_Straight_To_Payment_And_Waits_For_The_Manager_Review()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var id = await ParkedCarAsync(t);

        var reported = await t.Gates.ReportLostTicketAsync(id, Report(conditionsMet: true));

        reported.Status.Should().Be(SessionStatus.PendingPayment);
        reported.LostTicketStatus.Should().Be(LostTicketCaseStatus.Approved);
        reported.FinalFee.Should().BeGreaterThanOrEqualTo(CarLostTicketFee);
        var lostCase = Cases(t, id).Single();
        lostCase.ApprovalMode.Should().Be(LostTicketApproval.Conditional);
        lostCase.DecidedBy.Should().Be(t.StaffUserId);
        lostCase.IsResolved.Should().BeFalse("the Manager still reviews a conditional approval");
    }

    [Fact]
    public async Task A_Lost_Ticket_Can_Be_Reported_After_The_Fee_Was_Asked_At_The_Lane()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var id = await ParkedCarAsync(t);
        await t.Gates.RequestPaymentAsync(id, new CheckoutQuoteRequestModel());

        var reported = await t.Gates.ReportLostTicketAsync(id, Report());

        reported.Status.Should().Be(SessionStatus.LostTicketPending);
    }

    [Fact]
    public async Task A_Running_Online_Payment_Blocks_The_Report()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var id = await ParkedCarAsync(t);
        await t.Gates.PayOnlineAtLaneAsync(id, new LanePaymentModel { Provider = "VNPay" }, new PaymentRequestContext("127.0.0.1", "http://api"));

        var report = () => t.Gates.ReportLostTicketAsync(id, Report());

        await report.Should().ThrowAsync<BadRequestException>().WithMessage("*still in progress*");
        t.Db.ParkingSessions.Single(s => s.Id == id).Status.Should().Be(SessionStatus.PendingPayment);
    }

    [Fact]
    public async Task Rejecting_A_Case_Needs_A_Reason()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var id = await ParkedCarAsync(t);
        await t.Gates.ReportLostTicketAsync(id, Report());

        t.ActAs(t.ManagerUserId, "Manager");
        var reject = () => t.Gates.DecideLostTicketAsync(id, false, "  ");

        await reject.Should().ThrowAsync<BadRequestException>().WithMessage("A reason is required to reject a lost-ticket case.");
        t.Db.ParkingSessions.Single(s => s.Id == id).Status.Should().Be(SessionStatus.LostTicketPending);
        Cases(t, id).Single().Status.Should().Be(LostTicketCaseStatus.Pending);
    }

    [Fact]
    public async Task An_Approved_Case_Can_Still_Be_Charged_After_The_Payment_Request_Timed_Out()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var id = await ParkedCarAsync(t);
        await t.Gates.ReportLostTicketAsync(id, Report());
        t.ActAs(t.ManagerUserId, "Manager");
        await t.Gates.DecideLostTicketAsync(id, true, null);

        // Nobody paid: the job sends the session back to parked
        t.Clock.Advance(TimeSpan.FromMinutes(t.Options.Gate.PaymentLinkMinutes + 1));
        await t.Jobs.ReleaseStalePaymentRequestsAsync();
        t.Db.ParkingSessions.Single(s => s.Id == id).Status.Should().Be(SessionStatus.Active);

        t.ActAs(t.StaffUserId, "Staff");
        var quote = await t.Gates.RequestPaymentAsync(id, new CheckoutQuoteRequestModel());

        quote.Session.Status.Should().Be(SessionStatus.PendingPayment);
        quote.LostTicketFee.Should().Be(CarLostTicketFee);
    }
}
