using System.Threading.Tasks;
using FluentAssertions;
using PMS.Application.Exceptions;
using PMS.Application.Models.Operations;
using PMS.Application.Services;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>
/// Development / testing only: completing a pending VNPay or MoMo payment without the gateway, through
/// the same path as a verified gateway callback.
/// </summary>
public class PaymentSimulationTests
{
    private static readonly PaymentRequestContext Context = new("127.0.0.1", "http://api");

    private static async Task<OperationsTestContext> SeedAsync(bool allowSimulation)
    {
        var t = await new OperationsTestContext().SeedAsync();
        t.GatewayOptions.AllowSimulatedPayments = allowSimulation;
        t.ActAs(t.CustomerUserId, "User");
        return t;
    }

    private static Task<ReservationResponseModel> BookAsync(OperationsTestContext t) =>
        t.Reservations.CreateAsync(t.CustomerUserId, new CreateReservationModel
        {
            ParkingLotId = t.Lot.Id,
            SlotId = t.FreeCarSlot().Id,
            VehicleId = t.Car.Id,
            ArrivalTime = t.Clock.GetUtcNow().AddMinutes(60)
        });

    [Fact]
    public async Task Simulation_Is_Refused_When_The_Server_Does_Not_Allow_It()
    {
        var t = await SeedAsync(allowSimulation: false);
        var topUp = await t.Payments.CreateWalletTopUpPaymentAsync(t.CustomerUserId, new TopUpWalletModel { Amount = 100_000, Provider = "VNPay" }, Context);

        var act = () => t.Payments.SimulateSuccessAsync(topUp.Id);

        await act.Should().ThrowAsync<ForbiddenException>();
        t.Payments.SimulationEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task A_Simulated_Deposit_Confirms_The_Reservation()
    {
        var t = await SeedAsync(allowSimulation: true);
        var booked = await BookAsync(t);
        var deposit = await t.Payments.CreateDepositPaymentAsync(booked.Id, t.CustomerUserId, new CreatePaymentModel { Provider = "VNPay" }, Context);

        var result = await t.Payments.SimulateSuccessAsync(deposit.Id);

        result.Payment.Status.Should().Be(PaymentStatus.Succeeded);
        (await t.Reservations.GetAsync(t.CustomerUserId, booked.Id)).Status.Should().Be(ReservationStatus.Confirmed);
    }

    [Fact]
    public async Task A_Simulated_Top_Up_Credits_The_Wallet_Once()
    {
        var t = await SeedAsync(allowSimulation: true);
        var topUp = await t.Payments.CreateWalletTopUpPaymentAsync(t.CustomerUserId, new TopUpWalletModel { Amount = 200_000, Provider = "VNPay" }, Context);

        await t.Payments.SimulateSuccessAsync(topUp.Id);
        var again = await t.Payments.SimulateSuccessAsync(topUp.Id);

        again.Outcome.Should().Be("AlreadyProcessed");
        (await t.Payments.GetWalletAsync(t.CustomerUserId)).Balance.Should().Be(200_000);
    }

    [Fact]
    public async Task Without_Gateway_Keys_A_Payment_Can_Still_Be_Created_And_Simulated()
    {
        var t = await SeedAsync(allowSimulation: true);
        t.GatewayOptions.VnPay.TmnCode = "";
        t.GatewayOptions.VnPay.HashSecret = "";

        var topUp = await t.Payments.CreateWalletTopUpPaymentAsync(t.CustomerUserId, new TopUpWalletModel { Amount = 50_000, Provider = "VNPay" }, Context);
        topUp.PayUrl.Should().BeNull();

        await t.Payments.SimulateSuccessAsync(topUp.Id);
        (await t.Payments.GetWalletAsync(t.CustomerUserId)).Balance.Should().Be(50_000);
    }

    [Fact]
    public async Task Another_Customer_Cannot_Simulate_Someone_Elses_Payment()
    {
        var t = await SeedAsync(allowSimulation: true);
        var topUp = await t.Payments.CreateWalletTopUpPaymentAsync(t.CustomerUserId, new TopUpWalletModel { Amount = 100_000, Provider = "VNPay" }, Context);

        t.ActAs("someone-else", "User");
        var act = () => t.Payments.SimulateSuccessAsync(topUp.Id);

        await act.Should().ThrowAsync<NotFoundException>();
    }
}
