using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Audit.Application;
using Veritas.Web.Modules.Identity.Domain;
using Veritas.Web.Modules.Notification.Application;
using Veritas.Web.Modules.Notification.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.Identity.Application;

public sealed record LifecycleTransitionResult(bool Succeeded, string From, string To, string? Error);

public interface IUserLifecycleService
{
    Task<LifecycleTransitionResult> TransitionAsync(Guid userId, string targetState, string? reason, CancellationToken ct = default);
    Task<bool> CanTransitionAsync(string currentState, string targetState);
}

/// <summary>
/// The only place user lifecycle state may change (spec section 7). Enforces the
/// allowed transition table on the domain entity, audits every transition with
/// its previous value, bumps the security stamp so an already-signed-in session
/// picks up the new state at the next validation interval, and enqueues a
/// notification for account-affecting transitions.
/// </summary>
public sealed class UserLifecycleService : IUserLifecycleService
{
    private readonly VeritasDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAuditService _audit;
    private readonly INotificationService _notifications;

    public UserLifecycleService(
        VeritasDbContext db,
        UserManager<ApplicationUser> userManager,
        IAuditService audit,
        INotificationService notifications)
    {
        _db = db;
        _userManager = userManager;
        _audit = audit;
        _notifications = notifications;
    }

    public Task<bool> CanTransitionAsync(string currentState, string targetState) => Task.FromResult(
        UserLifecycleState.AllowedTransitions.TryGetValue(currentState, out var allowed)
        && allowed.Contains(targetState, StringComparer.OrdinalIgnoreCase));

    public async Task<LifecycleTransitionResult> TransitionAsync(Guid userId, string targetState, string? reason, CancellationToken ct = default)
    {
        var user = await _userManager.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return new LifecycleTransitionResult(false, "UNKNOWN", targetState, "User not found in this tenant.");

        var normalizedTarget = targetState.Trim().ToUpperInvariant();
        if (!await CanTransitionAsync(user.LifecycleState, normalizedTarget))
        {
            await _audit.RecordAsync("USER_LIFECYCLE_TRANSITION_REJECTED", user.Id.ToString(),
                user.LifecycleState, normalizedTarget, null, $"illegal-transition:{reason}", ct);

            return new LifecycleTransitionResult(false, user.LifecycleState, normalizedTarget,
                $"Transition {user.LifecycleState} -> {normalizedTarget} is not permitted.");
        }

        var previous = user.LifecycleState;
        user.LifecycleState = normalizedTarget;

        if (normalizedTarget == UserLifecycleState.Suspended)
        {
            user.SuspendedAtUtc = DateTimeOffset.UtcNow;
            user.SuspensionReason = reason;
            user.LockoutEnd = DateTimeOffset.UtcNow.AddYears(100);
        }
        else if (normalizedTarget == UserLifecycleState.Active)
        {
            user.SuspendedAtUtc = null;
            user.SuspensionReason = null;
            user.LockoutEnd = null;
        }
        else if (normalizedTarget == UserLifecycleState.Revoked)
        {
            user.LockoutEnd = DateTimeOffset.UtcNow.AddYears(100);
        }

        // Forces the cookie principal to be rebuilt on next validation, so a
        // suspended user does not keep operating on stale claims.
        await _userManager.UpdateSecurityStampAsync(user);
        await _userManager.UpdateAsync(user);

        await _audit.RecordAsync(
            action: $"USER_{normalizedTarget}",
            resourceId: user.Id.ToString(),
            previousValue: previous,
            newValue: normalizedTarget,
            decisionId: null,
            correlationId: reason ?? "lifecycle",
            ct: ct);

        if (normalizedTarget is UserLifecycleState.Suspended or UserLifecycleState.Revoked or UserLifecycleState.Active)
        {
            await _notifications.EnqueueAsync(
                user.Id,
                NotificationChannel.InApp,
                "ACCOUNT_LIFECYCLE_CHANGED",
                $"Your account is now {normalizedTarget}",
                reason is null ? $"Transition {previous} -> {normalizedTarget}." : $"Transition {previous} -> {normalizedTarget}: {reason}",
                ct);
        }

        return new LifecycleTransitionResult(true, previous, normalizedTarget, null);
    }
}
