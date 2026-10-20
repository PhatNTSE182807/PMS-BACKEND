using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using PMS.Application.Helpers.Parking;
using PMS.Application.Models.Operations;
using PMS.Application.Services.Payments;
using Xunit;

namespace PMS.Application.UnitTests.Operations;

/// <summary>VNPay HMAC-SHA512 and MoMo HMAC-SHA256 signatures, and the signed parking tokens.</summary>
public class PaymentSignatureTests
{
    private static readonly PaymentGatewayOptions Options = new()
    {
        VnPay = new VnPayOptions { TmnCode = "TESTCODE", HashSecret = "SECRETKEY123" },
        MoMo = new MoMoOptions { PartnerCode = "MOMO", AccessKey = "AK", SecretKey = "SK" }
    };

    private static VnPayGateway VnPay() => new(Options, Substitute.For<IHttpClientFactory>());

    private static MoMoGateway MoMo() => new(Options, Substitute.For<IHttpClientFactory>());

    [Fact]
    public void VnPay_Signs_The_Sorted_Url_Encoded_Fields_With_HmacSha512()
    {
        // Same order and encoding as the VNPay sample code (spaces as '+'); expected value from Node's crypto
        var query = VnPayGateway.BuildQuery(new Dictionary<string, string>
        {
            ["vnp_TxnRef"] = "PM123",
            ["vnp_Amount"] = "3000000",
            ["vnp_OrderInfo"] = "Coc dat cho RSV1",
            ["vnp_Command"] = "pay",
            ["vnp_TmnCode"] = "TESTCODE",
            ["vnp_BankCode"] = ""
        });

        query.Should().Be("vnp_Amount=3000000&vnp_Command=pay&vnp_OrderInfo=Coc+dat+cho+RSV1&vnp_TmnCode=TESTCODE&vnp_TxnRef=PM123");
        VnPayGateway.Sign("SECRETKEY123", query).Should().Be(
            "ea1848e320cb7cffa58bd6705cc4a826de595f9fea2bc80714e736227acf2e00d387a38cdf5e7ea55e7f50871443d4e4a547055bd63367358d0e9293b02d7ccb");
    }

    [Fact]
    public async Task VnPay_Payment_Url_Carries_A_Signature_The_Callback_Check_Accepts()
    {
        var checkout = await VnPay().CreateCheckoutAsync(new GatewayCheckoutRequest(
            "PM123", 30_000, "Coc dat cho RSV1", "http://localhost:5173/payment/return/vnpay", "http://api/ipn", "127.0.0.1",
            new DateTime(2026, 10, 20, 1, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 20, 1, 10, 0, DateTimeKind.Utc)));

        var uri = new Uri(checkout.PayUrl);
        uri.GetLeftPart(UriPartial.Path).Should().Be("https://sandbox.vnpayment.vn/paymentv2/vpcpay.html");
        var fields = ParseQuery(uri.Query);
        fields["vnp_Amount"].Should().Be("3000000");
        fields["vnp_CreateDate"].Should().Be("20261020080000", "VNPay dates are GMT+7");
        fields["vnp_ExpireDate"].Should().Be("20261020081000");
        VnPayGateway.VerifySignature("SECRETKEY123", fields).Should().BeTrue();
    }

    [Fact]
    public void VnPay_Callback_Is_Read_And_Tampering_Is_Detected()
    {
        var fields = SignedVnPayReturn("PM123", 3_000_000, "00");

        var callback = VnPay().ParseCallback(fields);
        callback.Should().BeEquivalentTo(new { SignatureValid = true, PaymentCode = "PM123", Amount = 30_000m, Success = true, TransactionNo = "14123456" });

        fields["vnp_Amount"] = "100";
        VnPay().ParseCallback(fields).SignatureValid.Should().BeFalse();
    }

    [Fact]
    public void VnPay_Failed_Response_Code_Is_Not_A_Success()
    {
        var callback = VnPay().ParseCallback(SignedVnPayReturn("PM123", 3_000_000, "24"));

        callback.SignatureValid.Should().BeTrue();
        callback.Success.Should().BeFalse();
    }

    [Fact]
    public void MoMo_Create_Signature_Uses_The_Documented_Key_Order()
    {
        var raw = MoMoGateway.CreateSignatureData("AK", "30000", "", "https://api/ipn", "PM1", "Coc", "MOMO", "https://web/ret", "PM1", "captureWallet");

        raw.Should().Be("accessKey=AK&amount=30000&extraData=&ipnUrl=https://api/ipn&orderId=PM1&orderInfo=Coc&partnerCode=MOMO&redirectUrl=https://web/ret&requestId=PM1&requestType=captureWallet");
        MoMoGateway.Sign("SK", raw).Should().Be("7cb438e1673b6e3f4c5a81ea2c18309eda9ce053b900bfaea731e4bf0169984c");
    }

    [Fact]
    public void MoMo_Ipn_Signature_Is_Verified_And_Result_Code_Zero_Is_Paid()
    {
        var fields = SignedMoMoIpn("PM1", "30000", "0");

        var callback = MoMo().ParseCallback(fields);
        callback.Should().BeEquivalentTo(new { SignatureValid = true, PaymentCode = "PM1", Amount = 30_000m, Success = true, TransactionNo = "4088878653" });

        fields["resultCode"] = "1006";
        MoMo().ParseCallback(fields).SignatureValid.Should().BeFalse();
    }

    [Fact]
    public void Parking_Tokens_Are_Bound_To_Their_Kind_And_Lot()
    {
        var signer = new ParkingTokenSigner(new ParkingOptions { TicketSigningKey = "unit-test-key" });
        var token = signer.Sign(ParkingTokenSigner.ReservationKind, 7, new Dictionary<string, string> { ["id"] = "42" });

        signer.Verify(token, ParkingTokenSigner.ReservationKind)!["id"].Should().Be("42");
        signer.Verify(token, ParkingTokenSigner.TicketKind).Should().BeNull();

        // Moving the payload to another lot breaks the signature
        var parts = token.Split('.');
        var forged = new ParkingTokenSigner(new ParkingOptions { TicketSigningKey = "other-key" }).Sign(ParkingTokenSigner.ReservationKind, 7, new Dictionary<string, string> { ["id"] = "42" });
        signer.Verify($"{parts[0]}.{parts[1]}.{forged.Split('.')[2]}", ParkingTokenSigner.ReservationKind).Should().BeNull();
    }

    /// <summary>A VNPay return query as the sandbox sends it, signed with the test secret.</summary>
    public static Dictionary<string, string> SignedVnPayReturn(string txnRef, long amountTimes100, string responseCode, string secret = "SECRETKEY123")
    {
        var fields = new Dictionary<string, string>
        {
            ["vnp_Amount"] = amountTimes100.ToString(),
            ["vnp_BankCode"] = "NCB",
            ["vnp_BankTranNo"] = "VNP14123456",
            ["vnp_CardType"] = "ATM",
            ["vnp_OrderInfo"] = "Coc dat cho",
            ["vnp_PayDate"] = "20261020080500",
            ["vnp_ResponseCode"] = responseCode,
            ["vnp_TmnCode"] = "TESTCODE",
            ["vnp_TransactionNo"] = "14123456",
            ["vnp_TransactionStatus"] = responseCode,
            ["vnp_TxnRef"] = txnRef
        };
        fields["vnp_SecureHashType"] = "HmacSHA512";
        fields["vnp_SecureHash"] = VnPayGateway.Sign(secret, VnPayGateway.BuildQuery(fields.Where(f => f.Key != "vnp_SecureHashType")));
        return fields;
    }

    /// <summary>A MoMo IPN body (as strings) signed with the test keys.</summary>
    public static Dictionary<string, string> SignedMoMoIpn(string orderId, string amount, string resultCode, string accessKey = "AK", string secretKey = "SK")
    {
        var fields = new Dictionary<string, string>
        {
            ["partnerCode"] = "MOMO",
            ["orderId"] = orderId,
            ["requestId"] = orderId,
            ["amount"] = amount,
            ["orderInfo"] = "Coc",
            ["orderType"] = "momo_wallet",
            ["transId"] = "4088878653",
            ["resultCode"] = resultCode,
            ["message"] = "Successful.",
            ["payType"] = "qr",
            ["responseTime"] = "1721720663942",
            ["extraData"] = ""
        };
        fields["signature"] = MoMoGateway.Sign(secretKey, MoMoGateway.CallbackSignatureData(accessKey, fields));
        return fields;
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
}
