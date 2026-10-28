using FluentValidation;
using PMS.Application.Models.Operations;

namespace PMS.Application.Models.Validators.Operations;

/// <summary>Lost ticket report at the gate (FR-OPS-05): the reason is stored on the case (500 characters).</summary>
public class LostTicketModelValidator : AbstractValidator<LostTicketModel>
{
    public LostTicketModelValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("A reason is required.")
            .MaximumLength(500).WithMessage("The reason must not exceed 500 characters.");
    }
}

/// <summary>
/// A Manager's decision on a session (lost-ticket approval or rejection, abnormal close). Whether a reason is
/// required depends on the action and is checked by the service; the stored note holds 500 characters.
/// </summary>
public class SessionDecisionModelValidator : AbstractValidator<SessionDecisionModel>
{
    public SessionDecisionModelValidator()
    {
        RuleFor(x => x.Reason)
            .MaximumLength(500).WithMessage("The reason must not exceed 500 characters.");
    }
}
