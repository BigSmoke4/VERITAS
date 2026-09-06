using FluentValidation;
using Veritas.Web.Modules.PrivilegedAccess.Presentation;

namespace Veritas.Web.Modules.PrivilegedAccess.Application.Validation;

public sealed class GrantTemporaryAccessDtoValidator : AbstractValidator<GrantTemporaryAccessDto>
{
    public GrantTemporaryAccessDtoValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.ResourceId).NotEmpty();
        RuleFor(x => x.PermissionKey)
            .NotEmpty()
            .Matches(@"^[a-z0-9_.\-]+\.[a-z0-9_\-]+$")
            .WithMessage("PermissionKey must follow the 'resource.action' convention, e.g. 'payment.read'.");
        // JIT access is meant to be short-lived: spec examples run 15-30 minutes.
        // A hard ceiling prevents "temporary" access from being used to grant
        // what is really permanent access without going through RBAC.
        RuleFor(x => x.DurationMinutes).InclusiveBetween(1, 24 * 60)
            .WithMessage("Temporary grants must be between 1 minute and 24 hours — for longer-lived access, assign a role instead.");
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(500);
    }
}
