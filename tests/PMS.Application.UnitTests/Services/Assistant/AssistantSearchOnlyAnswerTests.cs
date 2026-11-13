using FluentAssertions;
using PMS.Application.Models.Assistant;
using PMS.Application.Services.Assistant;
using Xunit;

namespace PMS.Application.UnitTests.Services.Assistant;

/// <summary>Answers without a model (no API key, or the quota is used up) still give each lot's prices.</summary>
public class AssistantSearchOnlyAnswerTests
{
    private static readonly LiveLotSnapshot Lots = new(
        "- Cong Hoa Parking Lot\n  Prices now:\n    4-seat car: 30,000 VND per hour", "v1", 1);

    private static readonly ScoredChunk Help = new(new KnowledgeChunk
    {
        Id = "deposit",
        File = "deposits-and-refunds.md",
        Title = "Deposits and refunds",
        Heading = "How much the deposit is",
        Content = "The deposit is 30,000 VND per car.",
    }, 0.8);

    [Theory]
    [InlineData("Giá gửi xe ô tô ở các bãi bao nhiêu?")]
    [InlineData("gia gui xe bao nhieu")]
    [InlineData("Bãi Cộng Hòa ở đâu?")]
    [InlineData("How much is parking at each lot?")]
    public void AsksAboutLots_IsTrue_ForLotAndPriceQuestions(string question) =>
        AssistantService.AsksAboutLots(question).Should().BeTrue();

    [Theory]
    [InlineData("Làm sao để đổi mật khẩu?")]
    [InlineData("How do I verify my vehicle?")]
    [InlineData("")]
    public void AsksAboutLots_IsFalse_ForOtherQuestions(string question) =>
        AssistantService.AsksAboutLots(question).Should().BeFalse();

    [Fact]
    public void SearchOnlyAnswer_StartsWithTheLotPrices_ForAPriceQuestion()
    {
        var answer = AssistantService.SearchOnlyAnswer("Giá bãi xe bao nhiêu?", [Help], Lots);

        answer.Should().StartWith("XXIV Parking lots and their prices now:");
        answer.Should().Contain("4-seat car: 30,000 VND per hour");
        answer.Should().Contain("How much the deposit is");
    }

    [Fact]
    public void SearchOnlyAnswer_GivesTheLotPrices_EvenWhenNoHelpMatches()
    {
        var answer = AssistantService.SearchOnlyAnswer("price of each lot", [], Lots);

        answer.Should().Contain("Cong Hoa Parking Lot");
        answer.Should().NotContain("I could not find this");
    }

    [Fact]
    public void SearchOnlyAnswer_LeavesTheLotsOut_ForOtherQuestions()
    {
        var answer = AssistantService.SearchOnlyAnswer("How do I verify my vehicle?", [Help], Lots);

        answer.Should().NotContain("Cong Hoa Parking Lot");
        answer.Should().StartWith("Here is what the XXIV Parking help says:");
    }
}
