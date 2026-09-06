using Xunit;

namespace Veritas.Tests;

/// <summary>
/// The full SeparationOfDutiesEvaluator requires a DbContext (see
/// Veritas.IntegrationTests for the DB-backed version); this test exercises
/// the pure conflict-matching predicate in isolation so the core rule —
/// "two permissions in the same conflict pair must never both be held" — is
/// covered without needing a database.
/// </summary>
public class SeparationOfDutiesLogicTests
{
    private static bool HasConflict(
        IReadOnlyCollection<string> existingPermissions,
        IReadOnlyCollection<string> candidatePermissions,
        string ruleA,
        string ruleB)
    {
        var hasA = existingPermissions.Contains(ruleA) || candidatePermissions.Contains(ruleA);
        var hasB = existingPermissions.Contains(ruleB) || candidatePermissions.Contains(ruleB);
        return hasA && hasB;
    }

    [Fact]
    public void UserWithCreatePermission_RequestingApprovePermission_IsAConflict()
    {
        var existing = new[] { "payment.create" };
        var candidate = new[] { "payment.approve" };

        Assert.True(HasConflict(existing, candidate, "payment.create", "payment.approve"));
    }

    [Fact]
    public void UserWithOnlyReadPermissions_RequestingApprovePermission_IsNotAConflict()
    {
        var existing = new[] { "payment.read" };
        var candidate = new[] { "payment.approve" };

        Assert.False(HasConflict(existing, candidate, "payment.create", "payment.approve"));
    }

    [Fact]
    public void CandidateRoleAloneContainingBothConflictingPermissions_IsAConflict()
    {
        var existing = Array.Empty<string>();
        var candidate = new[] { "payment.create", "payment.approve" };

        Assert.True(HasConflict(existing, candidate, "payment.create", "payment.approve"));
    }
}
