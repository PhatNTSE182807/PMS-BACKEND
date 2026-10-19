using System.Linq;
using FluentAssertions;
using PMS.Application.Helpers.VehicleDocuments;
using Xunit;

namespace PMS.Application.UnitTests.VehicleDocuments;

/// <summary>Normalisation of Vietnamese text and plate numbers read by OCR.</summary>
public class VehicleDocumentTextTests
{
    [Theory]
    [InlineData("NGUYỄN TẤN PHÁT", "NGUYEN TAN PHAT")]
    [InlineData("Đặng Thị Ánh Tuyết", "Dang Thi Anh Tuyet")]
    [InlineData("đỏ tươi", "do tuoi")]
    [InlineData("Hồ Chí Minh", "Ho Chi Minh")]
    public void Fold_Should_Remove_Diacritics_And_Keep_One_Char_Per_Char(string input, string expected)
    {
        var folded = VietnameseText.Fold(input);

        folded.Should().Be(expected);
        folded.Length.Should().Be(input.Normalize().Length);
    }

    [Fact]
    public void Fold_Should_Handle_Decomposed_Input()
    {
        // "Phát" typed with a combining acute accent (NFD), as some keyboards and OCR engines produce
        VietnameseText.Fold("Phát").Should().Be("Phat");
    }

    [Theory]
    [InlineData("  Nguyễn  tấn-Phát ", "NGUYEN TAN PHAT")]
    [InlineData("nguyen tan phat", "NGUYEN TAN PHAT")]
    public void NormalizeWords_Should_Ignore_Case_Diacritics_And_Punctuation(string input, string expected)
    {
        VietnameseText.NormalizeWords(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("51K-992.88", "51K99288")]
    [InlineData("51K99288", "51K99288")]
    [InlineData("51k 992 88", "51K99288")]
    [InlineData("29-B1 123.45", "29B112345")]
    [InlineData("30E-1234", "30E1234")]
    public void Normalize_Should_Strip_Separators(string plate, string expected)
    {
        PlateText.Normalize(plate).Should().Be(expected);
    }

    [Theory]
    [InlineData("5IK99288", "51K99288")]   // I read for 1 in the province
    [InlineData("51K992B8", "51K99288")]   // B read for 8 in the number
    [InlineData("S1K99288", "51K99288")]   // S read for 5
    [InlineData("518992 88", "51B99288")]  // 8 read for B in the series
    public void FixOcrConfusions_Should_Fix_Look_Alikes_By_Position(string read, string expected)
    {
        PlateText.FixOcrConfusions(PlateText.Normalize(read)).Should().Be(expected);
    }

    [Theory]
    [InlineData("51K-992.88", "51K99288", 1.0)]
    [InlineData("51K-992.88", "51K 992.88", 1.0)]
    [InlineData("51K-992.88", "5IK-992.88", 0.9)]
    [InlineData("51K-992.88", "51K-992.89", 0.0)]
    [InlineData("51K-992.88", "30E-123.45", 0.0)]
    public void ComparePlate_Should_Accept_Any_Format_Of_The_Same_Plate(string expected, string read, double score)
    {
        VehicleDocumentVerifier.ComparePlate(expected, read).Should().Be(score);
    }

    [Fact]
    public void FindPlates_Should_Find_A_Plate_Inside_A_Labelled_Line()
    {
        PlateText.FindPlates("Biển số đăng ký (Number plate): 51K-992.88").Should().Equal("51K99288");
        PlateText.FindPlates("Biển số: 59-X1 123.45").Should().Equal("59X112345");
    }

    [Fact]
    public void FindPlates_Should_Not_Take_A_Citizen_Id_For_A_Plate()
    {
        PlateText.FindPlates("Số / No.: 079204006789").Should().BeEmpty();
    }

    [Theory]
    [InlineData("51K99288", "51K-992.88")]
    [InlineData("30E1234", "30E-1234")]
    [InlineData("59X112345", "59X1-123.45")]
    public void Format_Should_Print_The_Usual_Separators(string normalized, string expected)
    {
        PlateText.Format(normalized).Should().Be(expected);
    }

    [Theory]
    [InlineData("Trắng", "Pearl White", 1.0)]
    [InlineData("XANH", "Gentian Blue Metallic", 1.0)]
    [InlineData("Xanh lá", "Green", 1.0)]
    [InlineData("Xanh dương", "Green", 0.0)]
    [InlineData("Đỏ", "Red", 1.0)]
    [InlineData("Đen", "White", 0.0)]
    [InlineData("Bạc", "Silver", 1.0)]
    public void CompareColor_Should_Match_Vietnamese_And_English_Colour_Names(string onPaper, string typed, double score)
    {
        VehicleDocumentVerifier.CompareColor(typed, onPaper).Should().Be(score);
    }

    [Theory]
    [InlineData("Porsche", "PORSCHE", true)]
    [InlineData("Mercedes", "MERCEDES-BENZ", true)]
    [InlineData("Toyota", "HONDA", false)]
    public void CompareBrand_Should_Ignore_Case_And_Accept_Longer_Official_Names(string typed, string onPaper, bool match)
    {
        (VehicleDocumentVerifier.CompareBrand(typed, onPaper) > 0).Should().Be(match);
    }

    [Theory]
    [InlineData("Camry", "CAMRY 2.5Q", true)]
    [InlineData("Taycan 4S", "TAYCAN 4S", true)]
    [InlineData("CR-V", "CRV L", true)]
    [InlineData("Camry", "VIOS", false)]
    public void CompareModel_Should_Accept_Model_Codes_That_Contain_The_Model(string typed, string onPaper, bool match)
    {
        (VehicleDocumentVerifier.CompareModel(typed, onPaper) > 0).Should().Be(match);
    }

    [Theory]
    [InlineData("Nguyễn Tấn Phát", "NGUYEN TAN PHAT", true)]
    [InlineData("Nguyễn Tấn Phát", "NGUYÊN TẢN PHÁT", true)]   // wrong diacritics from OCR
    [InlineData("Nguyễn Tấn Phát", "NGUYEN TAN PHA", true)]    // one letter lost
    [InlineData("Nguyễn Tấn Phát", "TRAN VAN BINH", false)]
    public void CompareName_Should_Ignore_Diacritics_And_Small_Ocr_Slips(string profile, string onPaper, bool match)
    {
        (VehicleDocumentVerifier.CompareName(profile, onPaper) > 0).Should().Be(match);
    }

    [Theory]
    [InlineData("079204006789", "079204006789", 1.0)]
    [InlineData("079204006789", "0792 0400 6789", 1.0)]
    [InlineData("079204006789", "079204006780", 0.0)]
    [InlineData("079204006789", "07920400678", 0.0)]
    public void CompareCitizenId_Should_Need_All_12_Digits(string profile, string onCard, double score)
    {
        VehicleDocumentVerifier.CompareCitizenId(profile, onCard).Should().Be(score);
    }

    [Fact]
    public void Similarity_Should_Be_One_For_Equal_Text()
    {
        VietnameseText.Similarity("ABC", "ABC").Should().Be(1);
        new[] { "ABC", "ABD" }.Select(s => VietnameseText.Similarity("ABC", s)).Last().Should().BeApproximately(0.667, 0.01);
    }
}
