using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using PMS.Application.Exceptions;
using PMS.Application.Helpers;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.Application.Services.Payments;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>Edge cases of the payment gateways, signed tickets, fee calculator and sign-in challenge tokens.</summary>
public class HelperBranchTests
{
    private static readonly TimeZoneInfo Vietnam = ParkingText.FindTimeZone("SE Asia Standard Time");

    // ---- VNPay ----

    private static PaymentGatewayOptions VnPayOptions(string tmnCode = "TESTCODE", string secret = "SECRET") =>
        new() { VnPay = new VnPayOptions { TmnCode = tmnCode, HashSecret = secret } };

    private static VnPayGateway VnPay(PaymentGatewayOptions options = null) =>
        new(options ?? VnPayOptions(), Substitute.For<IHttpClientFactory>());

    /// <summary>A return query signed with the test secret.</summary>
    private static Dictionary<string, string> SignedVnPay(string responseCode, string amount = "2000000", string status = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["vnp_TxnRef"] = "PAY-1",
            ["vnp_ResponseCode"] = responseCode,
            ["vnp_Amount"] = amount,
            ["vnp_TransactionNo"] = "123"
        };
        if (status != null)
            fields["vnp_TransactionStatus"] = status;
        fields["vnp_SecureHash"] = VnPayGateway.Sign("SECRET", VnPayGateway.BuildQuery(fields));
        return fields;
    }

    [Theory]
    [InlineData("00", "Transaction successful")]
    [InlineData("07", "Amount debited, but the transaction is flagged as suspicious")]
    [InlineData("09", "The card/account is not registered for Internet Banking")]
    [InlineData("10", "Card/account verification failed more than 3 times")]
    [InlineData("11", "The payment window has expired")]
    [InlineData("12", "The card/account is locked")]
    [InlineData("13", "Wrong transaction OTP")]
    [InlineData("24", "The customer cancelled the transaction")]
    [InlineData("51", "Insufficient balance")]
    [InlineData("65", "The daily transaction limit was exceeded")]
    [InlineData("75", "The paying bank is under maintenance")]
    [InlineData("79", "Wrong payment password too many times")]
    [InlineData("99", "Transaction failed")]
    [InlineData("01", "Transaction failed")]
    [InlineData("08", "Transaction failed")]
    [InlineData("14", "Transaction failed")]
    [InlineData("1X", "Transaction failed")]
    [InlineData("20", "Transaction failed")]
    [InlineData("25", "Transaction failed")]
    [InlineData("2X", "Transaction failed")]
    [InlineData("50", "Transaction failed")]
    [InlineData("52", "Transaction failed")]
    [InlineData("60", "Transaction failed")]
    [InlineData("66", "Transaction failed")]
    [InlineData("70", "Transaction failed")]
    [InlineData("76", "Transaction failed")]
    [InlineData("7X", "Transaction failed")]
    [InlineData("0X", "Transaction failed")]
    [InlineData("5X", "Transaction failed")]
    [InlineData("6X", "Transaction failed")]
    [InlineData("X0", "Transaction failed")]
    [InlineData("000", "Transaction failed")]
    [InlineData("", "Transaction failed")]
    public void VnPay_ParseCallback_Should_Explain_Every_Response_Code(string code, string message)
    {
        var callback = VnPay().ParseCallback(SignedVnPay(code));

        callback.SignatureValid.Should().BeTrue();
        callback.Message.Should().Be(message);
        callback.Success.Should().Be(code == "00");
    }

    [Fact]
    public void VnPay_ParseCallback_Should_Handle_A_Missing_Response_Code()
    {
        var fields = SignedVnPay("00");
        fields.Remove("vnp_ResponseCode");

        VnPay().ParseCallback(fields).Message.Should().Be("Transaction failed");
    }

    [Fact]
    public void VnPay_ParseCallback_Should_Read_Status_Amount_And_Signature()
    {
        var gateway = VnPay();

        gateway.ParseCallback(SignedVnPay("00", status: "00")).Success.Should().BeTrue();
        gateway.ParseCallback(SignedVnPay("00", status: "02")).Success.Should().BeFalse();
        gateway.ParseCallback(SignedVnPay("00", amount: "abc")).Amount.Should().Be(0);

        var unsigned = SignedVnPay("00");
        unsigned.Remove("vnp_SecureHash");
        gateway.ParseCallback(unsigned).SignatureValid.Should().BeFalse();

        var blankHash = SignedVnPay("00");
        blankHash["vnp_SecureHash"] = " ";
        gateway.ParseCallback(blankHash).SignatureValid.Should().BeFalse();
    }

    [Fact]
    public async Task VnPay_Should_Refuse_To_Work_Unconfigured_And_Default_The_Client_Ip()
    {
        var unconfigured = VnPay(VnPayOptions(tmnCode: " "));
        unconfigured.IsConfigured.Should().BeFalse();
        FluentActions.Invoking(() => unconfigured.ParseCallback(new Dictionary<string, string>()))
            .Should().Throw<InvalidOperationException>();
        VnPay(VnPayOptions(secret: null)).IsConfigured.Should().BeFalse();

        var checkout = await VnPay().CreateCheckoutAsync(new GatewayCheckoutRequest(
            "PAY-1", 20_000, "Parking fee", "http://app/return", "http://api/ipn", " ", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(10)));

        checkout.PayUrl.Should().Contain("vnp_IpAddr=127.0.0.1");
    }

    // ---- MoMo ----

    private static MoMoGateway MoMo(string partner = "MOMO", string access = "AK", string secret = "SK") =>
        new(new PaymentGatewayOptions { MoMo = new MoMoOptions { PartnerCode = partner, AccessKey = access, SecretKey = secret } },
            Substitute.For<IHttpClientFactory>());

    [Theory]
    [InlineData(null, "AK", "SK")]
    [InlineData("MOMO", " ", "SK")]
    [InlineData("MOMO", "AK", "")]
    public void MoMo_Should_Not_Be_Configured_With_A_Missing_Key(string partner, string access, string secret)
    {
        var gateway = MoMo(partner, access, secret);

        gateway.IsConfigured.Should().BeFalse();
        FluentActions.Invoking(() => gateway.ParseCallback(new Dictionary<string, string>()))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MoMo_ParseCallback_Should_Reject_Missing_Signatures_And_Read_Bad_Amounts_As_Zero()
    {
        var gateway = MoMo();

        var noSignature = gateway.ParseCallback(new Dictionary<string, string> { ["amount"] = "x", ["resultCode"] = "9000", ["orderId"] = null });
        noSignature.SignatureValid.Should().BeFalse();
        noSignature.Amount.Should().Be(0);
        noSignature.Success.Should().BeTrue();

        gateway.ParseCallback(new Dictionary<string, string> { ["signature"] = "  " }).SignatureValid.Should().BeFalse();
    }

    // ---- Signed tickets ----

    [Fact]
    public void ParkingTokenSigner_Should_Use_A_Random_Key_When_None_Is_Set()
    {
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<ParkingTokenSigner>>();
        var first = new ParkingTokenSigner(new ParkingOptions(), logger);
        var second = new ParkingTokenSigner(null);

        var token = first.Sign(ParkingTokenSigner.TicketKind, 1, new Dictionary<string, string> { ["s"] = "1" });

        first.Verify(token, ParkingTokenSigner.TicketKind).Should().NotBeNull();
        second.Verify(token, ParkingTokenSigner.TicketKind).Should().BeNull();
    }

    [Fact]
    public void ParkingTokenSigner_Should_Reject_Malformed_Tokens()
    {
        var signer = new ParkingTokenSigner(new ParkingOptions { TicketSigningKey = "key" });
        var token = signer.Sign(ParkingTokenSigner.TicketKind, 7, new Dictionary<string, string> { ["s"] = "1" });
        string Payload(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        signer.Verify(token, ParkingTokenSigner.TicketKind)!["lot"].Should().Be("7");
        signer.Verify(null, ParkingTokenSigner.TicketKind).Should().BeNull();
        signer.Verify(" ", ParkingTokenSigner.TicketKind).Should().BeNull();
        signer.Verify("a.b", ParkingTokenSigner.TicketKind).Should().BeNull();
        signer.Verify(token, ParkingTokenSigner.ExitPassKind).Should().BeNull();
        signer.Verify($"tkt.{Payload("null")}.sig", ParkingTokenSigner.TicketKind).Should().BeNull();
        signer.Verify($"tkt.{Payload("{\"s\":\"1\"}")}.sig", ParkingTokenSigner.TicketKind).Should().BeNull();
        signer.Verify($"tkt.{Payload("{\"lot\":\"x\"}")}.sig", ParkingTokenSigner.TicketKind).Should().BeNull();
        signer.Verify("tkt.!!!.sig", ParkingTokenSigner.TicketKind).Should().BeNull();
        signer.Verify($"tkt.{Payload("[1]")}.sig", ParkingTokenSigner.TicketKind).Should().BeNull();
    }

    // ---- Fee calculator ----

    private static DateTime Local(int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 20, hour, minute, 0), Vietnam);

    [Fact]
    public void FeeCalculator_Should_Default_To_Utc_And_60_Minute_Blocks()
    {
        var tariff = new Tariff { BlockMinutes = 0, BlockPrice = 10_000, FirstBlockMinutes = 0, FirstBlockPrice = 99_000 };
        var entry = new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);

        var fee = FeeCalculator.Calculate(tariff, entry, entry.AddMinutes(90), null);

        fee.Total.Should().Be(20_000);
        fee.Days.Should().ContainSingle().Which.Blocks.Should().Be(2);
    }

    [Fact]
    public void FeeCalculator_Should_Skip_A_First_Block_Without_A_Price()
    {
        var tariff = new Tariff { BlockPrice = 10_000, FirstBlockMinutes = 30, FirstBlockPrice = null };

        FeeCalculator.Calculate(tariff, Local(8), Local(9), Vietnam).Total.Should().Be(10_000);
    }

    [Fact]
    public void FeeCalculator_DayNight_Should_Price_Each_Block_By_Its_Start_Hour()
    {
        var tariff = new Tariff
        {
            ChargeModel = "DayNight", BlockMinutes = 60, BlockPrice = 1_000,
            DayStart = new TimeOnly(6, 0), NightStart = new TimeOnly(18, 0), DayPrice = 20_000, NightPrice = 30_000
        };

        // 17:00 block is day, 18:00 block is night
        FeeCalculator.Calculate(tariff, Local(17), Local(19), Vietnam).Total.Should().Be(50_000);
    }

    [Fact]
    public void FeeCalculator_DayNight_Should_Handle_A_Day_Window_Over_Midnight_And_Missing_Prices()
    {
        // "Day" here runs 22:00 → 06:00, so 23:00 is in the day window and 07:00 is not
        var tariff = new Tariff
        {
            ChargeModel = "daynight", BlockMinutes = 60, BlockPrice = 5_000,
            DayStart = new TimeOnly(22, 0), NightStart = new TimeOnly(6, 0), DayPrice = 20_000, NightPrice = null
        };

        FeeCalculator.Calculate(tariff, Local(23), Local(23, 30), Vietnam).Total.Should().Be(20_000);
        FeeCalculator.Calculate(tariff, Local(7), Local(7, 30), Vietnam).Total.Should().Be(5_000);
        FeeCalculator.Calculate(tariff, Local(3), Local(3, 30), Vietnam).Total.Should().Be(20_000);
    }

    [Fact]
    public void FeeCalculator_DayNight_Without_Hours_Should_Use_The_Block_Price()
    {
        var tariff = new Tariff { ChargeModel = "DayNight", BlockPrice = 7_000, DayStart = new TimeOnly(6, 0) };

        FeeCalculator.Calculate(tariff, Local(8), Local(9), Vietnam).Total.Should().Be(7_000);
    }

    // ---- Small helpers ----

    [Fact]
    public void Small_Helpers_Should_Handle_Blank_Input()
    {
        ParkingText.FindTimeZone(null).Should().NotBeNull();
        ParkingText.FindTimeZone("Nowhere/Unknown").Should().NotBeNull();
        LicensePlateHelper.IsValidVietnamesePlate(" ").Should().BeFalse();
        LicensePlateHelper.IsValidVietnamesePlate(null).Should().BeFalse();
        FluentActions.Invoking(() => ParkingStateMachines.EnsureSession(SessionStatus.Completed, SessionStatus.Active))
            .Should().Throw<BadRequestException>();
        FluentActions.Invoking(() => ParkingStateMachines.EnsureSession(null, SessionStatus.Active))
            .Should().Throw<BadRequestException>();
        ParkingStateMachines.EnsureSession(SessionStatus.Active, SessionStatus.PendingPayment);
    }

    [Fact]
    public void ParkingOptions_Should_Prefer_Configured_Tariffs()
    {
        new ParkingOptions().EffectiveTariffs.Should().BeSameAs(ParkingOptions.DefaultTariffs);
        new ParkingOptions { Tariffs = null }.EffectiveTariffs.Should().BeSameAs(ParkingOptions.DefaultTariffs);

        var custom = new[] { new DefaultTariff { VehicleTypeCode = "CAR_4", BlockPrice = 1 } };
        new ParkingOptions { Tariffs = custom }.EffectiveTariffs.Should().BeSameAs(custom);
    }

    // ---- Sign-in challenge tokens ----

    private static IConfiguration Config(string secret = "unit-test-secret-key-that-is-long-enough") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["JwtConfiguration:SecretKey"] = secret }).Build();

    /// <summary>A token signed with the real challenge key but the given claims.</summary>
    private static string ChallengeWith(IConfiguration configuration, params Claim[] claims) =>
        ChallengeWith(configuration, DateTime.UtcNow.AddMinutes(5), claims);

    private static string ChallengeWith(IConfiguration configuration, DateTime expires, params Claim[] claims)
    {
        var secret = configuration.GetValue<string>("JwtConfiguration:SecretKey") ?? "DefaultTwoFactorSecretKeyForSigningChallenges";
        var key = SHA256.HashData(Encoding.UTF8.GetBytes($"{secret}:PMS_2FA_CHALLENGE_PURPOSE"));
        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            NotBefore = expires.AddMinutes(-10),
            IssuedAt = expires.AddMinutes(-10),
            Expires = expires,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        }));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("no-at-sign", "no-at-sign")]
    [InlineData(" ", " ")]
    [InlineData("ab@example.com", "a*@example.com")]
    [InlineData("alice@example.com", "a***e@example.com")]
    public void MaskEmail_Should_Hide_The_Name(string email, string masked)
    {
        TwoFactorTokenHelper.MaskEmail(email).Should().Be(masked);
    }

    [Fact]
    public void TwoFactorToken_Should_Round_Trip_With_The_Default_Key_And_A_Custom_Expiry()
    {
        var empty = new ConfigurationBuilder().Build();
        var token = TwoFactorTokenHelper.GenerateTwoFactorToken("user-1", 42, "a***e@example.com", empty, TimeSpan.FromMinutes(1));

        var result = TwoFactorTokenHelper.ValidateTwoFactorToken(token, empty);

        result.isValid.Should().BeTrue();
        result.userId.Should().Be("user-1");
        result.otpId.Should().Be(42);
    }

    [Fact]
    public void TwoFactorToken_Should_Reject_Blank_Expired_Or_Tampered_Tokens()
    {
        var config = Config();

        TwoFactorTokenHelper.ValidateTwoFactorToken(" ", config).error.Should().Be("Challenge token is required.");
        TwoFactorTokenHelper.ValidateTwoFactorToken("not-a-jwt", config).error.Should().Be("Invalid or tampered verification challenge.");

        var expired = ChallengeWith(config, DateTime.UtcNow.AddMinutes(-10),
            new Claim(ClaimTypes.NameIdentifier, "user-1"), new Claim("otp_id", "1"), new Claim("purpose", TwoFactorTokenHelper.TwoFactorPurpose));
        TwoFactorTokenHelper.ValidateTwoFactorToken(expired, config).error.Should().StartWith("Verification challenge has expired");
    }

    [Fact]
    public void TwoFactorToken_Should_Reject_Missing_Or_Wrong_Claims()
    {
        var config = Config();
        var user = new Claim(ClaimTypes.NameIdentifier, "user-1");
        var otp = new Claim("otp_id", "5");
        var purpose = new Claim("purpose", TwoFactorTokenHelper.TwoFactorPurpose);

        TwoFactorTokenHelper.ValidateTwoFactorToken(ChallengeWith(config, otp, purpose), config)
            .error.Should().Be("Invalid challenge token claims.");
        TwoFactorTokenHelper.ValidateTwoFactorToken(ChallengeWith(config, user, purpose), config)
            .error.Should().Be("Invalid challenge token claims.");
        TwoFactorTokenHelper.ValidateTwoFactorToken(ChallengeWith(config, user, otp), config)
            .error.Should().Be("Invalid challenge token claims.");
        TwoFactorTokenHelper.ValidateTwoFactorToken(ChallengeWith(config, user, new Claim("otp_id", "abc"), purpose), config)
            .error.Should().Be("Invalid challenge token format.");

        var noDestination = TwoFactorTokenHelper.ValidateTwoFactorToken(ChallengeWith(config, user, otp, purpose), config);
        noDestination.isValid.Should().BeTrue();
        noDestination.destination.Should().BeNull();
    }
}
