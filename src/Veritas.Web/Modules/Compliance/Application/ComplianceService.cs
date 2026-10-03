using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Compliance.Domain;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.Compliance.Application;

public sealed record FindingSummary(
    Guid Id, string ControlType, string Severity, string Title, string Detail,
    string Status, DateTimeOffset FirstDetectedAtUtc, DateTimeOffset LastSeenAtUtc, string? Subject);

public sealed record ScanResult(int Created, int Updated, int AutoResolved, IReadOnlyList<FindingSummary> Findings);

public interface IComplianceService
{
    Task<IReadOnlyList<CompliancePolicy>> ListControlsAsync(CancellationToken ct = default);
    Task<CompliancePolicy> UpsertControlAsync(string controlType, string title, string description, string severity, int threshold, string? framework, bool enabled, CancellationToken ct = default);
    Task<ScanResult> ScanAsync(CancellationToken ct = default);
    Task<(IReadOnlyList<FindingSummary> Items, int Total)> ListFindingsAsync(string? status, string? controlType, int page, int pageSize, CancellationToken ct = default);
    Task AcknowledgeAsync(Guid findingId, CancellationToken ct = default);
}

/// <summary>
/// Dormant / excessive access detection (spec sections 26 and 31). Every finding
/// is produced by a query over real rows — last-login timestamps, role graph
/// fan-out, grant expiry columns — and is deduplicated by a stable key so
/// re-scanning updates `LastSeenAtUtc` instead of creating duplicates. Findings
/// that stop reproducing are auto-resolved, which is what makes the report
/// trustworthy rather than an ever-growing list.
/// </summary>
public sealed class ComplianceService : IComplianceService
{
    private readonly VeritasDbContext _db;
    private readonly ITenantContext _tenant;

    public ComplianceService(VeritasDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<IReadOnlyList<CompliancePolicy>> ListControlsAsync(CancellationToken ct = default) =>
        await _db.CompliancePolicies.AsNoTracking().OrderBy(c => c.ControlType).ToListAsync(ct);

    public async Task<CompliancePolicy> UpsertControlAsync(
        string controlType, string title, string description, string severity, int threshold,
        string? framework, bool enabled, CancellationToken ct = default)
    {
        var normalized = controlType.Trim().ToUpperInvariant();
        if (!ControlTypes.All.Contains(normalized))
            throw new InvalidOperationException($"Unknown control type '{normalized}'.");

        var existing = await _db.CompliancePolicies.FirstOrDefaultAsync(c => c.ControlType == normalized, ct);
        if (existing is null)
        {
            existing = new CompliancePolicy { OrganizationId = _tenant.OrganizationId, ControlType = normalized };
            _db.CompliancePolicies.Add(existing);
        }

        existing.Title = title.Trim();
        existing.Description = description.Trim();
        existing.Severity = severity.Trim().ToUpperInvariant();
        existing.Threshold = threshold;
        existing.Framework = framework;
        existing.Enabled = enabled;

        await _db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<ScanResult> ScanAsync(CancellationToken ct = default)
    {
        var controls = await _db.CompliancePolicies.Where(c => c.Enabled).ToListAsync(ct);
        var detected = new List<(string DedupeKey, CompliancePolicy Control, string Title, string Detail, Guid? UserId)>();
        var now = DateTimeOffset.UtcNow;

        foreach (var control in controls)
        {
            switch (control.ControlType)
            {
                case ControlTypes.InactiveUser:
                {
                    var cutoff = now.AddDays(-control.Threshold);
                    var inactive = await _db.Users.AsNoTracking()
                        .Where(u => u.LifecycleState == "ACTIVE"
                                    && (u.LastLoginAtUtc == null || u.LastLoginAtUtc < cutoff))
                        .Select(u => new { u.Id, u.DisplayName, u.LastLoginAtUtc })
                        .ToListAsync(ct);

                    foreach (var u in inactive)
                        detected.Add(($"inactive-user:{u.Id}", control, $"Inactive account: {u.DisplayName}",
                            u.LastLoginAtUtc is null
                                ? $"No recorded login; account created {control.Threshold}+ days ago."
                                : $"Last login {u.LastLoginAtUtc:u} UTC ({(int)(now - u.LastLoginAtUtc.Value).TotalDays} days ago).",
                            u.Id));
                    break;
                }

                case ControlTypes.DormantPrivilegedAccount:
                {
                    var cutoff = now.AddDays(-control.Threshold);
                    var privileged = await _db.UserRoles2.AsNoTracking()
                        .Where(ur => ur.ExpiresAtUtc == null || ur.ExpiresAtUtc > now)
                        .Where(ur => ur.Role.RolePermissions.Any(rp =>
                            rp.Permission.Key.EndsWith(".approve") || rp.Permission.Key.EndsWith(".delete")
                            || rp.Permission.Key.EndsWith(".manage")))
                        .Select(ur => ur.UserId).Distinct().ToListAsync(ct);

                    foreach (var userId in privileged)
                    {
                        var lastActivity = await _db.AuthorizationDecisions.AsNoTracking()
                            .Where(d => d.SubjectUserId == userId.ToString() && d.EvaluatedAtUtc >= cutoff)
                            .Select(d => (DateTimeOffset?)d.EvaluatedAtUtc).MaxAsync(ct);

                        if (lastActivity is not null) continue;

                        var name = await _db.Users.AsNoTracking()
                            .Where(u => u.Id == userId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);

                        detected.Add(($"dormant-privileged:{userId}", control,
                            $"Dormant privileged account: {name ?? userId.ToString()}",
                            $"Holds an approve/delete/manage permission but has no authorization decision in the last {control.Threshold} days.",
                            userId));
                    }
                    break;
                }

                case ControlTypes.UnusedPermission:
                {
                    var unused = await _db.Permissions.AsNoTracking()
                        .Where(p => !_db.RolePermissions.Any(rp => rp.PermissionId == p.Id))
                        .Select(p => new { p.Id, p.Key })
                        .ToListAsync(ct);

                    foreach (var p in unused)
                        detected.Add(($"unused-permission:{p.Id}", control, $"Unused permission: {p.Key}",
                            "No role grants this permission, so no subject can hold it.", null));
                    break;
                }

                case ControlTypes.OrphanedAccount:
                {
                    var orphaned = await _db.Users.AsNoTracking()
                        .Where(u => u.LifecycleState == "ACTIVE"
                                    && (u.DepartmentId == null
                                        || !_db.Departments.Any(d => d.Id == u.DepartmentId)))
                        .Select(u => new { u.Id, u.DisplayName })
                        .ToListAsync(ct);

                    foreach (var u in orphaned)
                        detected.Add(($"orphaned-account:{u.Id}", control, $"Orphaned account: {u.DisplayName}",
                            "Active account with no resolvable owning department — no manager can review its access.", u.Id));
                    break;
                }

                case ControlTypes.ExcessivePrivileges:
                {
                    var heavy = await _db.UserRoles2.AsNoTracking()
                        .Where(ur => ur.ExpiresAtUtc == null || ur.ExpiresAtUtc > now)
                        .GroupBy(ur => ur.UserId)
                        .Select(g => new { UserId = g.Key, RoleCount = g.Count() })
                        .ToListAsync(ct);

                    foreach (var h in heavy.Where(x => x.RoleCount > control.Threshold))
                    {
                        var name = await _db.Users.AsNoTracking()
                            .Where(u => u.Id == h.UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
                        detected.Add(($"excessive-privileges:{h.UserId}", control,
                            $"Excessive privileges: {name ?? h.UserId.ToString()}",
                            $"Holds {h.RoleCount} concurrent roles (threshold {control.Threshold}).", h.UserId));
                    }
                    break;
                }

                case ControlTypes.ExpiredTemporaryAccess:
                {
                    var stuck = await _db.TemporaryGrants.AsNoTracking()
                        .Where(g => !g.Revoked && g.ExpiresAtUtc <= now)
                        .Select(g => new { g.Id, g.UserId, g.ExpiresAtUtc })
                        .ToListAsync(ct);

                    foreach (var g in stuck)
                        detected.Add(($"expired-grant:{g.Id}", control, "Temporary grant past expiry and not revoked",
                            $"Grant {g.Id:N} expired {g.ExpiresAtUtc:u} UTC but is still unrevoked — the expiration worker has not caught up.",
                            g.UserId));
                    break;
                }
            }
        }

        var existingFindings = await _db.ComplianceFindings
            .Where(f => f.Status != ComplianceFindingStatus.Resolved)
            .ToListAsync(ct);

        var detectedKeys = detected.Select(d => d.DedupeKey).ToHashSet(StringComparer.Ordinal);
        var byKey = existingFindings.ToDictionary(f => f.DedupeKey, StringComparer.Ordinal);

        var created = 0; var updated = 0; var autoResolved = 0;

        foreach (var (key, control, title, detail, userId) in detected)
        {
            if (byKey.TryGetValue(key, out var finding))
            {
                finding.LastSeenAtUtc = now;
                finding.Detail = detail;
                finding.Title = title;
                finding.Severity = control.Severity;
                updated++;
            }
            else
            {
                _db.ComplianceFindings.Add(new ComplianceFinding
                {
                    OrganizationId = _tenant.OrganizationId,
                    CompliancePolicyId = control.Id,
                    ControlType = control.ControlType,
                    Severity = control.Severity,
                    Title = title,
                    Detail = detail,
                    SubjectUserId = userId,
                    DedupeKey = key
                });
                created++;
            }
        }

        foreach (var finding in existingFindings.Where(f => !detectedKeys.Contains(f.DedupeKey)))
        {
            finding.Status = ComplianceFindingStatus.Resolved;
            finding.ResolvedAtUtc = now;
            autoResolved++;
        }

        await _db.SaveChangesAsync(ct);

        var items = await ProjectFindingsAsync(_db.ComplianceFindings.AsNoTracking()
            .OrderByDescending(f => f.LastSeenAtUtc).Take(50), ct);

        return new ScanResult(created, updated, autoResolved, items);
    }

    public async Task<(IReadOnlyList<FindingSummary> Items, int Total)> ListFindingsAsync(
        string? status, string? controlType, int page, int pageSize, CancellationToken ct = default)
    {
        var query = _db.ComplianceFindings.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(f => f.Status == status);
        if (!string.IsNullOrWhiteSpace(controlType)) query = query.Where(f => f.ControlType == controlType);

        var total = await query.CountAsync(ct);
        var items = await ProjectFindingsAsync(
            query.OrderByDescending(f => f.LastSeenAtUtc).Skip((Math.Max(page, 1) - 1) * pageSize).Take(pageSize), ct);

        return (items, total);
    }

    public async Task AcknowledgeAsync(Guid findingId, CancellationToken ct = default)
    {
        var finding = await _db.ComplianceFindings.FirstOrDefaultAsync(f => f.Id == findingId, ct)
            ?? throw new InvalidOperationException("Finding not found in this tenant.");
        finding.Status = ComplianceFindingStatus.Acknowledged;
        await _db.SaveChangesAsync(ct);
    }

    private async Task<IReadOnlyList<FindingSummary>> ProjectFindingsAsync(IQueryable<ComplianceFinding> query, CancellationToken ct)
    {
        var rows = await query.ToListAsync(ct);
        var userIds = rows.Where(r => r.SubjectUserId is not null).Select(r => r.SubjectUserId!.Value).Distinct().ToList();
        var names = userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.DisplayName }).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        return rows.Select(f => new FindingSummary(
            f.Id, f.ControlType, f.Severity, f.Title, f.Detail, f.Status,
            f.FirstDetectedAtUtc, f.LastSeenAtUtc,
            f.SubjectUserId is not null && names.TryGetValue(f.SubjectUserId.Value, out var n) ? n : null)).ToList();
    }
}
