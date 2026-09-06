using FluentValidation;
using Veritas.Web.Modules.Authorization.Presentation.Controllers;

namespace Veritas.Web.Modules.Authorization.Application.Validation;

/// <summary>Validates the shape of an /api/v1/authorize request before it ever reaches AuthorizationService.</summary>
public sealed class AuthorizeRequestDtoValidator : AbstractValidator<AuthorizeRequestDto>
{
    public AuthorizeRequestDtoValidator()
    {
        RuleFor(x => x.Subject).NotEmpty().Must(BeAGuid).WithMessage("Subject must be a valid GUID user id.");
        RuleFor(x => x.Resource).NotEmpty().Must(BeAGuid).WithMessage("Resource must be a valid GUID resource id.");
        RuleFor(x => x.Action).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Environment).NotEmpty().MaximumLength(32);
        RuleFor(x => x.Context!.Ip)
            .Must(ip => string.IsNullOrEmpty(ip) || System.Net.IPAddress.TryParse(ip, out _))
            .When(x => x.Context is not null)
            .WithMessage("Context.Ip must be a valid IP address if provided.");
        RuleFor(x => x.Context!.DeviceTrust)
            .Must(v => string.IsNullOrEmpty(v) || new[] { "LOW", "MEDIUM", "HIGH" }.Contains(v.ToUpperInvariant()))
            .When(x => x.Context is not null)
            .WithMessage("Context.DeviceTrust must be LOW, MEDIUM, or HIGH if provided.");
    }

    private static bool BeAGuid(string value) => Guid.TryParse(value, out _);
}
