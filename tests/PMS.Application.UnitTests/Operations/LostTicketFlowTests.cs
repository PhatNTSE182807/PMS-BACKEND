using System.IO;
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
/// Lost ticket / card (FR-OPS-05, SRS 4.2.4): the Staff decides every case (team decision 2026-11-04, no Manager
/// approval): on the spot (BR-13), after checking the papers, and a rejected vehicle staying in the lot.
/// </summary>
public class LostTicketFlowTests
{
    private const string Plate = "51G-555.66";

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

    private static LostTicketReviewModel Approve(string note = null) => new() { Approve = true, Note = note };

    private static LostTicketReviewModel Refuse(string note) => new() { Approve = false, Note = note };

    [Fact]
    public async Task A_Staff_Approval_Leaves_The_Vehicle_Parked_With_The_Fine_On_Its_Bill()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var id = await ParkedCarAsync(t);

        var reported = await t.Gates.ReportLostTicketAsync(id, Report());

        reported.Status.Should().Be(SessionStatus.LostTicketPending);
        reported.LostTicketStatus.Should().Be(LostTicketCaseStatus.Pending);
        reported.LostTicketApprovalMode.Should().Be(LostTicketApproval.Staff, "the Staff decides lost tickets, not the Manager");
        reported.LostTicketReportedBy.Should().Be(t.StaffUserId);
        reported.LostTicketReason.Should().Be("Paper ticket lost; registration and ID checked");

        var decided = await t.Gates.ReviewLostTicketAsync(id, Approve("Papers match the entry photo"));

        decided.Status.Should().Be(SessionStatus.Active, "the vehicle stays until it checks out");
        decided.LostTicketStatus.Should().Be(LostTicketCaseStatus.Approved);
        decided.LostTicketDecisionNote.Should().Be("Papers match the entry photo");
        var lostCase = Cases(t, id).Single();
        lostCase.ApprovalMode.Should().Be(LostTicketApproval.Staff);
        lostCase.DecidedBy.Should().Be(t.StaffUserId);
        lostCase.DecidedAt.Should().NotBeNull();
        lostCase.IsResolved.Should().BeTrue("no Manager review follows");

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

        var rejected = await t.Gates.ReviewLostTicketAsync(id, Refuse("Registration does not match the driver"));

        rejected.Status.Should().Be(SessionStatus.Active);
        rejected.LostTicketStatus.Should().Be(LostTicketCaseStatus.Rejected);

        // No payment point and no exit lane takes the fee while the rejection stands
        var quote = () => t.Gates.RequestPaymentAsync(id, new CheckoutQuoteRequestModel());
        var cash = () => t.Gates.PayCashAsync(id, new CashPaymentModel { Amount = 1_000_000 });
        var exit = () => t.Gates.CheckoutAsync(id, new CheckoutModel { PlateNumber = Plate });
        await quote.Should().ThrowAsync<BadRequestException>().WithMessage("*was rejected*");
        await cash.Should().ThrowAsync<BadRequestException>().WithMessage("*was rejected*");
        await exit.Should().ThrowAsync<BadRequestException>().WithMessage("*was rejected*");
        t.Db.ParkingSessions.Single(s => s.Id == id).Status.Should().Be(SessionStatus.Active);

        // The owner comes back with full papers: a new case, approved this time
        await t.Gates.ReportLostTicketAsync(id, Report());
        await t.Gates.ReviewLostTicketAsync(id, Approve());

        var due = await t.Gates.RequestPaymentAsync(id, new CheckoutQuoteRequestModel());
        due.LostTicketFee.Should().Be(CarLostTicketFee, "the fine is charged once, however many cases were opened");
        await t.Gates.PayCashAsync(id, new CashPaymentModel { Amount = due.AmountDue });
        var left = await t.Gates.CheckoutAsync(id, new CheckoutModel { PlateNumber = Plate });

        left.BarrierOpen.Should().BeTrue();
        left.Session.Status.Should().Be(SessionStatus.Completed);
        Cases(t, id).Select(c => c.Status).Should().Equal(LostTicketCaseStatus.Rejected, LostTicketCaseStatus.Approved);
    }

    [Fact]
    public async Task A_Case_Left_Waiting_For_A_Manager_Is_Decided_By_The_Staff()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var id = await ParkedCarAsync(t);
        await t.Gates.ReportLostTicketAsync(id, Report());
        // A case reported before the change, still waiting for a Manager
        Cases(t, id).Single().ApprovalMode = LostTicketApproval.Manager;
        await t.Db.SaveChangesAsync();

        var decided = await t.Gates.ReviewLostTicketAsync(id, Approve());

        decided.LostTicketStatus.Should().Be(LostTicketCaseStatus.Approved);
        var lostCase = Cases(t, id).Single();
        lostCase.ApprovalMode.Should().Be(LostTicketApproval.Staff);
        lostCase.DecidedBy.Should().Be(t.StaffUserId);
    }

    [Fact]
    public async Task A_Conditional_Approval_Leaves_The_Vehicle_Parked_With_No_Manager_Review()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var id = await ParkedCarAsync(t);

        var reported = await t.Gates.ReportLostTicketAsync(id, Report(conditionsMet: true));

        reported.Status.Should().Be(SessionStatus.Active);
        reported.LostTicketStatus.Should().Be(LostTicketCaseStatus.Approved);
        var lostCase = Cases(t, id).Single();
        lostCase.ApprovalMode.Should().Be(LostTicketApproval.Conditional);
        lostCase.DecidedBy.Should().Be(t.StaffUserId);
        lostCase.IsResolved.Should().BeTrue("the Staff's on-the-spot approval is final");

        var quote = await t.Gates.RequestPaymentAsync(id, new CheckoutQuoteRequestModel());
        quote.LostTicketFee.Should().Be(CarLostTicketFee, "the fine is added when the vehicle checks out");
    }

    private static LostTicketPapersModel Papers() => new()
    {
        Reason = "Paper ticket lost",
        // The smallest valid JPEG and PNG headers are enough for the type check
        CitizenIdPhoto = new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0]),
        CitizenIdLength = 8,
        RegistrationPhoto = new MemoryStream([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        RegistrationLength = 8
    };

    [Fact]
    public async Task Papers_Wait_For_The_Staff_Who_Approves_And_The_Fine_Goes_On_The_Bill()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var id = await ParkedCarAsync(t);

        var reported = await t.Gates.ReportLostTicketWithPapersAsync(id, Papers());
        reported.Status.Should().Be(SessionStatus.LostTicketPending);
        reported.LostTicketApprovalMode.Should().Be(LostTicketApproval.Staff);
        reported.LostTicketHasPapers.Should().BeTrue();
        var (content, type) = await t.Gates.GetLostTicketPaperAsync(id, "citizen-id");
        type.Should().Be("image/jpeg");
        await content.DisposeAsync();

        var approved = await t.Gates.ReviewLostTicketAsync(id, new LostTicketReviewModel { Approve = true });

        approved.Status.Should().Be(SessionStatus.Active);
        approved.LostTicketStatus.Should().Be(LostTicketCaseStatus.Approved);
        Cases(t, id).Single().ApprovalMode.Should().Be(LostTicketApproval.Staff);
        Cases(t, id).Single().IsResolved.Should().BeTrue();
        var quote = await t.Gates.RequestPaymentAsync(id, new CheckoutQuoteRequestModel());
        quote.LostTicketFee.Should().Be(CarLostTicketFee);
    }

    [Fact]
    public async Task Refused_Papers_Keep_The_Vehicle_In_The_Lot()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var id = await ParkedCarAsync(t);
        await t.Gates.ReportLostTicketWithPapersAsync(id, Papers());

        var noReason = () => t.Gates.ReviewLostTicketAsync(id, new LostTicketReviewModel { Approve = false });
        await noReason.Should().ThrowAsync<BadRequestException>().WithMessage("Say why you refuse the lost ticket*");

        var refused = await t.Gates.ReviewLostTicketAsync(id, new LostTicketReviewModel { Approve = false, Note = "Name on the ID differs" });

        refused.Status.Should().Be(SessionStatus.Active);
        refused.LostTicketStatus.Should().Be(LostTicketCaseStatus.Rejected);
        var quote = () => t.Gates.RequestPaymentAsync(id, new CheckoutQuoteRequestModel());
        await quote.Should().ThrowAsync<BadRequestException>().WithMessage("*was rejected*");
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

        var reject = () => t.Gates.ReviewLostTicketAsync(id, Refuse("  "));

        await reject.Should().ThrowAsync<BadRequestException>().WithMessage("Say why you refuse the lost ticket*");
        t.Db.ParkingSessions.Single(s => s.Id == id).Status.Should().Be(SessionStatus.LostTicketPending);
        Cases(t, id).Single().Status.Should().Be(LostTicketCaseStatus.Pending);
    }

    [Fact]
    public async Task An_Approved_Case_Can_Still_Be_Charged_After_The_Payment_Request_Timed_Out()
    {
        var t = await new OperationsTestContext().SeedAsync();
        var id = await ParkedCarAsync(t);
        await t.Gates.ReportLostTicketAsync(id, Report());
        await t.Gates.ReviewLostTicketAsync(id, Approve());

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
