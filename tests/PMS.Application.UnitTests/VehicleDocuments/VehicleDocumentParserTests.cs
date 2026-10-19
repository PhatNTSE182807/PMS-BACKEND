using System;
using System.Linq;
using FluentAssertions;
using PMS.Application.Helpers.VehicleDocuments;
using PMS.Application.Services.Ocr;
using Xunit;

namespace PMS.Application.UnitTests.VehicleDocuments;

/// <summary>Reading cà vẹt and CCCD fields from OCR lines, and the verification outcome.</summary>
public class VehicleDocumentParserTests
{
    private static OcrLine[] Lines(params string[] text) => text.Select(t => new OcrLine(t, 0.95)).ToArray();

    private static readonly OcrLine[] Certificate = Lines(
        "GIẤY CHỨNG NHẬN ĐĂNG KÝ XE Ô TÔ",
        "Tên chủ xe (Owner's full name): NGUYỄN TẤN PHÁT",
        "Địa chỉ (Address): 12 Lê Lợi, Quận 1, TP. Hồ Chí Minh",
        "Nhãn hiệu (Brand): PORSCHE   Số loại (Model code): TAYCAN 4S",
        "Màu sơn (Color): XANH",
        "Biển số đăng ký (Number plate): 51K-992.88");

    private static readonly OcrLine[] Card = Lines(
        "CĂN CƯỚC CÔNG DÂN",
        "Số / No.: 079204006789",
        "Họ và tên / Full name:",
        "NGUYỄN TẤN PHÁT",
        "Ngày sinh / Date of birth: 15/03/2004",
        "Giới tính / Sex: Nam   Quốc tịch / Nationality: Việt Nam");

    private static VerificationExpectations Expected(Action<VerificationExpectationsBuilder> change = null)
    {
        var builder = new VerificationExpectationsBuilder();
        change?.Invoke(builder);
        return builder.Build();
    }

    [Fact]
    public void RegistrationCertificate_Should_Read_Every_Field_Including_Two_On_One_Line()
    {
        var data = RegistrationCertificateParser.Parse(Certificate);

        data.OwnerName.Value.Should().Be("NGUYỄN TẤN PHÁT");
        data.Brand.Value.Should().Be("PORSCHE");
        data.Model.Value.Should().Be("TAYCAN 4S");
        data.Color.Value.Should().Be("XANH");
        data.Plate.Value.Should().Be("51K-992.88");
        data.Plate.Confidence.Should().Be(0.95);
    }

    [Fact]
    public void RegistrationCertificate_Should_Read_Values_Printed_Under_Their_Label()
    {
        var data = RegistrationCertificateParser.Parse(Lines("Nhãn hiệu", "HONDA", "Biển số", "29-B1 123.45"));

        data.Brand.Value.Should().Be("HONDA");
        data.Plate.Value.Should().Be("29B1-123.45");
    }

    [Fact]
    public void RegistrationCertificate_Should_Find_An_Unlabelled_Plate_Anywhere()
    {
        RegistrationCertificateParser.Parse(Lines("some header", "51K 99288")).Plate.Value.Should().Be("51K-992.88");
    }

    [Fact]
    public void CitizenIdCard_Should_Read_Number_Name_On_Next_Line_And_Birth_Date()
    {
        var data = CitizenIdCardParser.Parse(Card);

        data.Number.Value.Should().Be("079204006789");
        data.FullName.Value.Should().Be("NGUYỄN TẤN PHÁT");
        data.DateOfBirth.Value.Should().Be("2004-03-15");
    }

    [Fact]
    public void CitizenIdCard_Should_Read_Grouped_Digits_And_Fix_Look_Alikes()
    {
        CitizenIdCardParser.Parse(Lines("Số: 0792 O400 6789")).Number.Value.Should().Be("079204006789");
    }

    [Fact]
    public void CitizenIdCard_Should_Fall_Back_To_The_Mrz_On_The_Back()
    {
        var data = CitizenIdCardParser.Parse(Lines(
            "IDVNM2040067895079204006789<<8",
            "0403156M4403150VNM<<<<<<<<<<<4",
            "NGUYEN<<TAN<PHAT<<<<<<<<<<<<<<"));

        data.Number.Value.Should().Be("079204006789");
        data.FullName.Value.Should().Be("NGUYEN TAN PHAT");
    }

    [Fact]
    public void Verify_Should_AutoVerify_When_Everything_Matches_With_High_Confidence()
    {
        var outcome = Verify(Certificate, Card, Expected());

        outcome.Status.Should().Be("AutoVerified");
        outcome.Fields.Should().OnlyContain(f => f.Result == "Match");
        outcome.Confidence.Should().BeGreaterThanOrEqualTo(0.85);
    }

    [Fact]
    public void Verify_Should_Send_A_Different_Plate_To_Review()
    {
        var outcome = Verify(Certificate, Card, Expected(e => e.Plate = "30E-123.45"));

        outcome.Status.Should().Be("NeedsReview");
        outcome.Fields.Single(f => f.Field == "plate").Result.Should().Be("Mismatch");
        outcome.Reason.Should().Contain("Plate number");
    }

    [Fact]
    public void Verify_Should_Send_A_Citizen_Id_That_Is_Not_The_Profiles_To_Review()
    {
        var outcome = Verify(Certificate, Card, Expected(e => e.CitizenId = "079204006780"));

        outcome.Status.Should().Be("NeedsReview");
        outcome.Fields.Single(f => f.Field == "citizenId").Result.Should().Be("Mismatch");
    }

    [Fact]
    public void Verify_Should_Never_AutoVerify_On_Low_Confidence()
    {
        var blurry = Certificate.Select(l => l with { Confidence = 0.6 }).ToArray();

        var outcome = Verify(blurry, Card, Expected());

        outcome.Status.Should().Be("NeedsReview");
        outcome.Fields.Where(f => f.Document == "RegistrationCertificate").Should().OnlyContain(f => f.Result == "Match");
        outcome.Reason.Should().Contain("low confidence");
    }

    [Fact]
    public void Verify_Should_Mark_Very_Low_Confidence_Values_Unreadable()
    {
        var unreadable = Certificate.Select(l => l with { Confidence = 0.2 }).ToArray();

        var outcome = Verify(unreadable, Card, Expected());

        outcome.Status.Should().Be("NeedsReview");
        outcome.Fields.Where(f => f.Document == "RegistrationCertificate").Should().OnlyContain(f => f.Result == "Unreadable");
    }

    [Fact]
    public void Verify_Should_Reject_Papers_That_Cannot_Be_Read_At_All()
    {
        var outcome = Verify([], [], Expected());

        outcome.Status.Should().Be("Rejected");
        outcome.Fields.Should().OnlyContain(f => f.Result == "Unreadable");
    }

    [Fact]
    public void Verify_Should_Send_A_Vehicle_In_Someone_Elses_Name_To_Review()
    {
        var outcome = Verify(Certificate, Card, Expected(e => e.OwnedBySomeoneElse = true));

        outcome.Status.Should().Be("NeedsReview");
        outcome.Fields.Single(f => f.Field == "ownerName").Note.Should().Contain("another person");
    }

    [Fact]
    public void Verify_Should_Compare_The_Birth_Date_Only_When_The_Profile_Has_One()
    {
        Verify(Certificate, Card, Expected()).Fields.Should().NotContain(f => f.Field == "dateOfBirth");

        var outcome = Verify(Certificate, Card, Expected(e => e.DateOfBirth = new DateOnly(2004, 3, 16)));
        outcome.Fields.Single(f => f.Field == "dateOfBirth").Result.Should().Be("Mismatch");
    }

    private static VerificationOutcome Verify(OcrLine[] certificate, OcrLine[] card, VerificationExpectations expected) =>
        VehicleDocumentVerifier.Verify(RegistrationCertificateParser.Parse(certificate), CitizenIdCardParser.Parse(card), expected);

    /// <summary>The vehicle form and profile that match <see cref="Certificate"/> and <see cref="Card"/>.</summary>
    private class VerificationExpectationsBuilder
    {
        public string Plate = "51K99288";
        public string CitizenId = "079204006789";
        public bool OwnedBySomeoneElse;
        public DateOnly? DateOfBirth;

        public VerificationExpectations Build() => new()
        {
            Plate = Plate,
            Brand = "Porsche",
            Model = "Taycan 4S",
            Color = "Gentian Blue Metallic",
            ProfileFullName = "Nguyễn Tấn Phát",
            ProfileCitizenId = CitizenId,
            ProfileDateOfBirth = DateOfBirth,
            OwnedBySomeoneElse = OwnedBySomeoneElse
        };
    }
}
