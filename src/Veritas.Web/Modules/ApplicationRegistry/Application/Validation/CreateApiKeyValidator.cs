using FluentValidation;
using Veritas.Web.Modules.ApplicationRegistry.Presentation;

namespace Veritas.Web.Modules.ApplicationRegistry.Application.Validation;

public sealed class CreateApiKeyDtoValidator : AbstractValidator<CreateApiKeyDto>
{
    public CreateApiKeyDtoValidator()
    {
        RuleFor(x => x.ServiceAccountId).NotEmpty();
        RuleFor(x => x.Scopes).NotNull();
        RuleForEach(x => x.Scopes).NotEmpty().MaximumLength(64);
        RuleFor(x => x.TtlHours)
            .GreaterThan(0)
            .When(x => x.TtlHours.HasValue)
            .WithMessage("TtlHours, if provided, must be positive.");
    }
}
