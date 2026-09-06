using Veritas.Web.Modules.Authorization.Application.Validation;
using Veritas.Web.Modules.Authorization.Presentation.Controllers;
using Veritas.Web.Modules.PrivilegedAccess.Application.Validation;
using Veritas.Web.Modules.PrivilegedAccess.Presentation;
using Xunit;

namespace Veritas.Tests;

public class ValidatorTests
{
    [Fact]
    public void AuthorizeRequest_WithNonGuidSubject_FailsValidation()
    {
        var dto = new AuthorizeRequestDto("not-a-guid", Guid.NewGuid().ToString(), "read", "production", null);
        var result = new AuthorizeRequestDtoValidator().Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Subject");
    }

    [Fact]
    public void AuthorizeRequest_WithValidGuidsAndAction_Passes()
    {
        var dto = new AuthorizeRequestDto(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "read", "production", null);
        var result = new AuthorizeRequestDtoValidator().Validate(dto);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void AuthorizeRequest_WithInvalidDeviceTrust_FailsValidation()
    {
        var dto = new AuthorizeRequestDto(
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "read", "production",
            new AuthorizeContextDto(null, "SUPER_TRUSTED", null));
        var result = new AuthorizeRequestDtoValidator().Validate(dto);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void GrantTemporaryAccess_WithDurationOverOneDay_FailsValidation()
    {
        var dto = new GrantTemporaryAccessDto(Guid.NewGuid(), Guid.NewGuid(), "payment.read", 60 * 25, "Incident review");
        var result = new GrantTemporaryAccessDtoValidator().Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "DurationMinutes");
    }

    [Fact]
    public void GrantTemporaryAccess_WithMalformedPermissionKey_FailsValidation()
    {
        var dto = new GrantTemporaryAccessDto(Guid.NewGuid(), Guid.NewGuid(), "PaymentRead", 30, "Incident review");
        var result = new GrantTemporaryAccessDtoValidator().Validate(dto);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "PermissionKey");
    }

    [Fact]
    public void GrantTemporaryAccess_WithValidShortLivedGrant_Passes()
    {
        var dto = new GrantTemporaryAccessDto(Guid.NewGuid(), Guid.NewGuid(), "payment.read", 30, "Incident INC-20482");
        var result = new GrantTemporaryAccessDtoValidator().Validate(dto);

        Assert.True(result.IsValid);
    }
}
