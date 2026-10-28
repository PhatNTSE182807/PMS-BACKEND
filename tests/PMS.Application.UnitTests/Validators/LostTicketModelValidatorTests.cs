using FluentAssertions;
using PMS.Application.Models.Operations;
using PMS.Application.Models.Validators.Operations;
using Xunit;

namespace PMS.Application.UnitTests.Validators;

public class LostTicketModelValidatorTests
{
    private readonly LostTicketModelValidator _report = new();
    private readonly SessionDecisionModelValidator _decision = new();

    [Fact]
    public void Report_WithReason_IsValid()
    {
        _report.Validate(new LostTicketModel { Reason = "Paper ticket lost; registration and ID checked", ConditionsMet = true })
            .IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Report_WithoutReason_IsRejected(string reason)
    {
        var result = _report.Validate(new LostTicketModel { Reason = reason });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.ErrorMessage == "A reason is required.");
    }

    [Fact]
    public void Report_WithTooLongReason_IsRejected()
    {
        _report.Validate(new LostTicketModel { Reason = new string('x', 501) }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Decision_WithoutReason_IsValid()
    {
        // An approval may come without remarks; the service asks for a reason only on rejection
        _decision.Validate(new SessionDecisionModel()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Decision_WithTooLongReason_IsRejected()
    {
        _decision.Validate(new SessionDecisionModel { Reason = new string('x', 501) }).IsValid.Should().BeFalse();
    }
}
