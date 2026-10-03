using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.Audit.Application;
using Veritas.Web.Modules.ResourceManagement.Domain;
// This file's own namespace ends in `.Application`, so the bare identifier
// `Application` binds to the namespace, not to the registry entity.
using ApplicationEntity = Veritas.Web.Modules.ResourceManagement.Domain.Application;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Modules.ResourceManagement.Application;

public sealed record ApplicationSummary(
    Guid Id, string Name, string Owner, string Environment, string Status,
    int ResourceCount, int ServiceAccountCount, int ApiKeyCount, string RiskTier);

public sealed record ResourceSummary(
    Guid Id, string Name, string ResourceType, string Classification, string Environment,
    string? OwnerDepartment, string PermissionKeyPrefix, Guid ApplicationId, string ApplicationName);

public sealed record ApplicationDetail(
    Guid Id, string Name, string Owner, string Environment, string Status, string RiskTier,
    IReadOnlyList<ResourceSummary> Resources,
    IReadOnlyList<(Guid Id, string Name, string Owner, string Status, DateTimeOffset? LastUsedAtUtc, DateTimeOffset? ExpiresAtUtc)> ServiceAccounts,
    DateTimeOffset? LastAuthorizationAtUtc, int AuthorizationCount30d);

public interface IResourceService
{
    Task<IReadOnlyList<ApplicationSummary>> ListApplicationsAsync(CancellationToken ct = default);
    Task<ApplicationDetail?> GetApplicationAsync(Guid applicationId, CancellationToken ct = default);
    Task<IReadOnlyList<ResourceSummary>> ListResourcesAsync(Guid? applicationId, string? classification, CancellationToken ct = default);
    Task<ApplicationEntity> CreateApplicationAsync(string name, string owner, string environment, CancellationToken ct = default);
    Task<Resource> CreateResourceAsync(Guid applicationId, string name, string type, string classification, string environment, string? ownerDepartment, string permissionKeyPrefix, CancellationToken ct = default);
}

public sealed class ResourceService : IResourceService
{
    private static readonly HashSet<string> SensitiveActions = new(StringComparer.OrdinalIgnoreCase)
        { "delete", "approve", "write", "manage", "revoke", "grant" };

    private readonly VeritasDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IAuditService _audit;

    public ResourceService(VeritasDbContext db, ITenantContext tenant, IAuditService audit)
    {
        _db = db;
        _tenant = tenant;
        _audit = audit;
    }

    public async Task<IReadOnlyList<ApplicationSummary>> ListApplicationsAsync(CancellationToken ct = default)
    {
        var apps = await _db.Applications.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);

        var resourceCounts = await _db.Resources.AsNoTracking()
            .GroupBy(r => r.ApplicationId).Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);

        var saCounts = await _db.ServiceAccounts.AsNoTracking()
            .GroupBy(s => s.ApplicationId).Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);

        var keyCounts = await _db.ApiKeys.AsNoTracking()
            .GroupBy(k => k.ServiceAccount.ApplicationId).Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);

        var highRiskPrefixes = await _db.Resources.AsNoTracking()
            .Where(r => r.Classification == "HIGHLY_CONFIDENTIAL" || r.Environment == "production")
            .Select(r => r.ApplicationId).Distinct().ToListAsync(ct);

        return apps.Select(a => new ApplicationSummary(
            a.Id, a.Name, a.Owner, a.Environment, a.Status,
            resourceCounts.TryGetValue(a.Id, out var rc) ? rc : 0,
            saCounts.TryGetValue(a.Id, out var sc) ? sc : 0,
            keyCounts.TryGetValue(a.Id, out var kc) ? kc : 0,
            highRiskPrefixes.Contains(a.Id) ? "HIGH" : "STANDARD")).ToList();
    }

    public async Task<ApplicationDetail?> GetApplicationAsync(Guid applicationId, CancellationToken ct = default)
    {
        var app = await _db.Applications.AsNoTracking().FirstOrDefaultAsync(a => a.Id == applicationId, ct);
        if (app is null) return null;

        var resources = await ListResourcesAsync(applicationId, null, ct);

        var serviceAccounts = await _db.ServiceAccounts.AsNoTracking()
            .Where(s => s.ApplicationId == applicationId)
            .Select(s => new { s.Id, s.Name, s.Owner, s.Status, s.LastUsedAtUtc, s.ExpiresAtUtc })
            .ToListAsync(ct);

        // ResourceId is text on the decision record; compare as text so the
        // predicate stays translatable by Npgsql (no Guid.Parse in SQL).
        var resourceIdStrings = resources.Select(r => r.Id.ToString()).ToList();
        var since = DateTimeOffset.UtcNow.AddDays(-30);

        var decisions = await _db.AuthorizationDecisions.AsNoTracking()
            .Where(d => resourceIdStrings.Contains(d.ResourceId) && d.EvaluatedAtUtc >= since)
            .Select(d => new { d.EvaluatedAtUtc })
            .ToListAsync(ct);

        return new ApplicationDetail(
            app.Id, app.Name, app.Owner, app.Environment, app.Status,
            resources.Any(r => r.Classification == "HIGHLY_CONFIDENTIAL") ? "HIGH" : "STANDARD",
            resources,
            serviceAccounts.Select(s => (s.Id, s.Name, s.Owner, s.Status, s.LastUsedAtUtc, s.ExpiresAtUtc)).ToList(),
            decisions.Count == 0 ? null : decisions.Max(d => d.EvaluatedAtUtc),
            decisions.Count);
    }

    public async Task<IReadOnlyList<ResourceSummary>> ListResourcesAsync(Guid? applicationId, string? classification, CancellationToken ct = default)
    {
        var query = _db.Resources.AsNoTracking().AsQueryable();
        if (applicationId is not null) query = query.Where(r => r.ApplicationId == applicationId);
        if (!string.IsNullOrWhiteSpace(classification)) query = query.Where(r => r.Classification == classification);

        var resources = await query.OrderBy(r => r.Name).ToListAsync(ct);
        var appNames = await _db.Applications.AsNoTracking()
            .Select(a => new { a.Id, a.Name }).ToDictionaryAsync(a => a.Id, a => a.Name, ct);

        return resources.Select(r => new ResourceSummary(
            r.Id, r.Name, r.ResourceType, r.Classification, r.Environment, r.OwnerDepartment,
            r.PermissionKeyPrefix, r.ApplicationId,
            appNames.TryGetValue(r.ApplicationId, out var an) ? an : "(unknown)")).ToList();
    }

    public async Task<ApplicationEntity> CreateApplicationAsync(string name, string owner, string environment, CancellationToken ct = default)
    {
        var app = new ApplicationEntity
        {
            OrganizationId = _tenant.OrganizationId,
            Name = name.Trim(),
            Owner = owner.Trim(),
            Environment = environment.Trim().ToLowerInvariant()
        };
        _db.Applications.Add(app);
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("APPLICATION_CREATED", app.Id.ToString(), null, app.Name, null, app.Id.ToString(), ct);
        return app;
    }

    public async Task<Resource> CreateResourceAsync(
        Guid applicationId, string name, string type, string classification, string environment,
        string? ownerDepartment, string permissionKeyPrefix, CancellationToken ct = default)
    {
        var resource = new Resource
        {
            OrganizationId = _tenant.OrganizationId,
            ApplicationId = applicationId,
            Name = name.Trim(),
            ResourceType = type.Trim().ToLowerInvariant(),
            Classification = classification.Trim().ToUpperInvariant(),
            Environment = environment.Trim().ToLowerInvariant(),
            OwnerDepartment = ownerDepartment,
            PermissionKeyPrefix = permissionKeyPrefix.Trim().ToLowerInvariant()
        };

        _db.Resources.Add(resource);
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync("RESOURCE_CREATED", resource.Id.ToString(), null,
            $"{resource.Name} ({resource.Classification})", null, resource.Id.ToString(), ct);
        return resource;
    }

    /// <summary>Exposed for the compliance scanner: which resources carry destructive actions.</summary>
    internal static bool IsSensitiveAction(string action) => SensitiveActions.Contains(action);
}
