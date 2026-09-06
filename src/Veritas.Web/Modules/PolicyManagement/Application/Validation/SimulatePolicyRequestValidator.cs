using FluentValidation;
using Veritas.Web.Modules.PolicyManagement.Presentation.Controllers;

namespace Veritas.Web.Modules.PolicyManagement.Application.Validation;

public sealed class SimulatePolicyRequestDtoValidator : AbstractValidator<SimulatePolicyRequestDto>
{
    public SimulatePolicyRequestDtoValidator()
    {
        RuleFor(x => x.CandidatePolicyVersionId).NotEmpty();
        RuleFor(x => x.ResourceId).NotEmpty();
        RuleFor(x => x.Action).NotEmpty().MaximumLength(64);
    }
}
