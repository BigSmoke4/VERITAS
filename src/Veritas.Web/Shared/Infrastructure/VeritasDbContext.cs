using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.AccessReview.Domain;
using Veritas.Web.Modules.ApplicationRegistry.Domain;
using Veritas.Web.Modules.Audit.Domain;
using Veritas.Web.Modules.Compliance.Domain;
using Veritas.Web.Modules.Identity.Domain;
using Veritas.Web.Modules.Notification.Domain;
using Veritas.Web.Modules.PolicyManagement.Domain;
using Veritas.Web.Modules.PrivilegedAccess.Domain;
using Veritas.Web.Modules.ResourceManagement.Domain;
using Veritas.Web.Modules.RiskManagement.Domain;
using Veritas.Web.Modules.RoleManagement.Domain;
using Veritas.Web.Shared.Domain;
using OrgEntities = Veritas.Web.Modules.Organization.Domain;
using AccessRequestEntities = Veritas.Web.Modules.AccessRequest.Domain;

namespace Veritas.Web.Shared.Infrastructure;

/// <summary>
/// Single PostgreSQL model for the modular monolith (ADR-002). All modules
/// share this context by design; the module boundary is a *code* boundary
/// (see docs/module-boundaries.md), not an assembly boundary.
///
/// Two invariants are enforced here rather than left to callers:
///  1. every ITenantOwned entity gets a global tenant query filter, and
///  2. every AuditableEntity additionally gets a soft-delete filter,
/// so a forgotten WHERE clause cannot leak another tenant's — or a
/// soft-deleted — row.
/// </summary>
public class VeritasDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    private readonly ITenantContext _tenant;

    public VeritasDbContext(DbContextOptions<VeritasDbContext> options, ITenantContext tenant)
        : base(options)
    {
        _tenant = tenant;
    }

    // --- Organization --------------------------------------------------------
    public DbSet<OrgEntities.Organization> Organizations => Set<OrgEntities.Organization>();
    public DbSet<OrgEntities.Department> Departments => Set<OrgEntities.Department>();

    // --- Roles & permissions -------------------------------------------------
    public DbSet<Role> Roles2 => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles2 => Set<UserRole>();
    public DbSet<SoDConflictRule> SoDConflictRules => Set<SoDConflictRule>();

    // --- Application registry & resources ------------------------------------
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<ServiceAccount> ServiceAccounts => Set<ServiceAccount>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<ApiKeyScope> ApiKeyScopes => Set<ApiKeyScope>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<ResourceTypeDefinition> ResourceTypes => Set<ResourceTypeDefinition>();
    public DbSet<ResourceGroup> ResourceGroups => Set<ResourceGroup>();

    // --- Policy engine -------------------------------------------------------
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<PolicyVersion> PolicyVersions => Set<PolicyVersion>();
    public DbSet<PolicyRule> PolicyRules => Set<PolicyRule>();
    public DbSet<PolicyCondition> PolicyConditions => Set<PolicyCondition>();

    // --- Access requests & approvals -----------------------------------------
    public DbSet<AccessRequestEntities.AccessRequest> AccessRequests => Set<AccessRequestEntities.AccessRequest>();
    public DbSet<AccessRequestEntities.ApprovalStep> ApprovalSteps => Set<AccessRequestEntities.ApprovalStep>();

    // --- Privileged / JIT access ---------------------------------------------
    public DbSet<TemporaryGrant> TemporaryGrants => Set<TemporaryGrant>();
    public DbSet<PrivilegedAccessRequest> PrivilegedAccessRequests => Set<PrivilegedAccessRequest>();
    public DbSet<PrivilegedSession> PrivilegedSessions => Set<PrivilegedSession>();

    // --- Risk ----------------------------------------------------------------
    public DbSet<RiskEvaluation> RiskEvaluations => Set<RiskEvaluation>();
    public DbSet<RiskSignalObservation> RiskSignalObservations => Set<RiskSignalObservation>();

    // --- Access review -------------------------------------------------------
    public DbSet<AccessReviewCampaign> AccessReviewCampaigns => Set<AccessReviewCampaign>();
    public DbSet<AccessReviewItem> AccessReviewItems => Set<AccessReviewItem>();

    // --- Audit & detection ---------------------------------------------------
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AuthorizationDecisionRecord> AuthorizationDecisions => Set<AuthorizationDecisionRecord>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();

    // --- Compliance ----------------------------------------------------------
    public DbSet<CompliancePolicy> CompliancePolicies => Set<CompliancePolicy>();
    public DbSet<ComplianceFinding> ComplianceFindings => Set<ComplianceFinding>();

    // --- Notification --------------------------------------------------------
    public DbSet<NotificationRecord> Notifications => Set<NotificationRecord>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        ConfigureKeysAndIndexes(builder);
        ConfigureRelationships(builder);
        ConfigureCheckConstraints(builder);
        ConfigureConcurrency(builder);
        ConfigureTenantIsolation(builder);
    }

    private static void ConfigureKeysAndIndexes(ModelBuilder builder)
    {
        builder.Entity<RolePermission>().HasKey(rp => new { rp.RoleId, rp.PermissionId });
        builder.Entity<UserRole>().HasKey(ur => new { ur.UserId, ur.RoleId });

        // Natural keys that must be unique per tenant.
        builder.Entity<OrgEntities.Organization>().HasIndex(o => o.Slug).IsUnique();
        builder.Entity<OrgEntities.Department>().HasIndex(d => new { d.OrganizationId, d.Name }).IsUnique();
        builder.Entity<Permission>().HasIndex(p => new { p.OrganizationId, p.Key }).IsUnique();
        builder.Entity<Role>().HasIndex(r => new { r.OrganizationId, r.Name }).IsUnique();
        builder.Entity<Application>().HasIndex(a => new { a.OrganizationId, a.Name }).IsUnique();
        builder.Entity<Resource>().HasIndex(r => new { r.OrganizationId, r.Name }).IsUnique();
        builder.Entity<ResourceTypeDefinition>().HasIndex(t => new { t.OrganizationId, t.Key }).IsUnique();
        builder.Entity<ResourceGroup>().HasIndex(g => new { g.OrganizationId, g.Name }).IsUnique();
        builder.Entity<Policy>().HasIndex(p => new { p.OrganizationId, p.Name }).IsUnique();
        builder.Entity<PolicyVersion>().HasIndex(pv => new { pv.PolicyId, pv.VersionNumber }).IsUnique();
        builder.Entity<ApprovalStep>().HasIndex(s => new { s.AccessRequestId, s.Order }).IsUnique();
        builder.Entity<SoDConflictRule>().HasIndex(r => new { r.OrganizationId, r.PermissionKeyA, r.PermissionKeyB }).IsUnique();
        builder.Entity<ServiceAccount>().HasIndex(s => new { s.OrganizationId, s.Name }).IsUnique();
        builder.Entity<ApiKey>().HasIndex(k => k.KeyHash).IsUnique();
        builder.Entity<ApiKeyScope>().HasIndex(s => new { s.ApiKeyId, s.Scope }).IsUnique();
        builder.Entity<AccessReviewItem>().HasIndex(i => new { i.AccessReviewCampaignId, i.SubjectUserId, i.PermissionKey }).IsUnique();
        builder.Entity<SecurityEvent>().HasIndex(e => new { e.OrganizationId, e.DedupeKey }).IsUnique();
        builder.Entity<ComplianceFinding>().HasIndex(f => new { f.OrganizationId, f.DedupeKey }).IsUnique();
        builder.Entity<CompliancePolicy>().HasIndex(c => new { c.OrganizationId, c.ControlType }).IsUnique();
        builder.Entity<WebhookSubscription>().HasIndex(w => new { w.OrganizationId, w.EventType, w.TargetUrl }).IsUnique();

        // Hot read paths. Authorization is the latency-critical surface, so its
        // lookup columns are indexed explicitly rather than left to chance.
        builder.Entity<TemporaryGrant>().HasIndex(g => new { g.UserId, g.ResourceId, g.PermissionKey });
        builder.Entity<TemporaryGrant>().HasIndex(g => new { g.Revoked, g.ExpiresAtUtc });
        builder.Entity<AuthorizationDecisionRecord>().HasIndex(d => new { d.OrganizationId, d.EvaluatedAtUtc });
        builder.Entity<AuthorizationDecisionRecord>().HasIndex(d => d.SubjectUserId);
        builder.Entity<AuthorizationDecisionRecord>().HasIndex(d => d.PolicyVersionId);
        builder.Entity<AuditLog>().HasIndex(a => new { a.OrganizationId, a.TimestampUtc });
        builder.Entity<AuditLog>().HasIndex(a => a.Action);
        builder.Entity<AuditLog>().HasIndex(a => a.CorrelationId);
        builder.Entity<AuditLog>().HasIndex(a => a.ActorUserId);
        builder.Entity<AccessRequestEntities.AccessRequest>().HasIndex(r => new { r.OrganizationId, r.Status });
        builder.Entity<AccessRequestEntities.AccessRequest>().HasIndex(r => r.RequestedByUserId);
        builder.Entity<RiskEvaluation>().HasIndex(r => new { r.UserId, r.EvaluatedAtUtc });
        builder.Entity<RiskSignalObservation>().HasIndex(s => new { s.UserId, s.SignalType });
        builder.Entity<NotificationRecord>().HasIndex(n => new { n.Status, n.AttemptCount });
        builder.Entity<AccessReviewCampaign>().HasIndex(c => new { c.OrganizationId, c.ReviewerUserId });
        builder.Entity<AccessReviewItem>().HasIndex(i => new { i.AccessReviewCampaignId, i.Decision });
        builder.Entity<PrivilegedAccessRequest>().HasIndex(p => new { p.OrganizationId, p.Status });
        builder.Entity<UserRole>().HasIndex(ur => new { ur.UserId, ur.ExpiresAtUtc });
    }

    private static void ConfigureRelationships(ModelBuilder builder)
    {
        // Organization hierarchy (self-referencing; no cascade — deleting a
        // department must not silently delete its children).
        builder.Entity<OrgEntities.Department>()
            .HasOne<OrgEntities.Department>()
            .WithMany()
            .HasForeignKey(d => d.ParentDepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Application>()
            .HasMany(a => a.Resources)
            .WithOne(r => r.Application)
            .HasForeignKey(r => r.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ResourceGroup>()
            .HasOne<Application>()
            .WithMany()
            .HasForeignKey(g => g.ApplicationId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<ServiceAccount>()
            .HasOne<Application>()
            .WithMany()
            .HasForeignKey(s => s.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ApiKey>()
            .HasMany(k => k.Scopes)
            .WithOne(s => s.ApiKey)
            .HasForeignKey(s => s.ApiKeyId)
            .OnDelete(DeleteBehavior.Cascade);

        // Role graph.
        builder.Entity<Role>()
            .HasMany(r => r.RolePermissions)
            .WithOne(rp => rp.Role)
            .HasForeignKey(rp => rp.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<RolePermission>()
            .HasOne(rp => rp.Permission)
            .WithMany()
            .HasForeignKey(rp => rp.PermissionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<UserRole>()
            .HasOne(ur => ur.Role)
            .WithMany()
            .HasForeignKey(ur => ur.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        // Policy version tree: Policy -> PolicyVersion -> PolicyRule -> PolicyCondition.
        builder.Entity<Policy>()
            .HasMany(p => p.Versions)
            .WithOne(v => v.Policy)
            .HasForeignKey(v => v.PolicyId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<PolicyVersion>()
            .HasMany(v => v.Rules)
            .WithOne()
            .HasForeignKey(r => r.PolicyVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<PolicyRule>()
            .HasMany(r => r.Conditions)
            .WithOne()
            .HasForeignKey(c => c.PolicyRuleId)
            .OnDelete(DeleteBehavior.Cascade);

        // Access request -> approval chain.
        builder.Entity<AccessRequestEntities.AccessRequest>()
            .HasMany(r => r.Steps)
            .WithOne()
            .HasForeignKey(s => s.AccessRequestId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<AccessReviewCampaign>()
            .HasMany(c => c.Items)
            .WithOne()
            .HasForeignKey(i => i.AccessReviewCampaignId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<PrivilegedSession>()
            .HasOne<PrivilegedAccessRequest>()
            .WithMany()
            .HasForeignKey(s => s.PrivilegedAccessRequestId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ComplianceFinding>()
            .HasOne(f => f.CompliancePolicy)
            .WithMany()
            .HasForeignKey(f => f.CompliancePolicyId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureCheckConstraints(ModelBuilder builder)
    {
        // Constraints that make invalid state unrepresentable at the storage
        // layer, so no code path — including a future one — can write it.
        builder.Entity<TemporaryGrant>().ToTable(t =>
            t.HasCheckConstraint("CK_TemporaryGrant_Window", "\"ExpiresAtUtc\" > \"StartAtUtc\""));

        builder.Entity<AccessRequestEntities.AccessRequest>().ToTable(t =>
            t.HasCheckConstraint("CK_AccessRequest_Duration", "\"Duration\" > interval '0'"));

        builder.Entity<RiskEvaluation>().ToTable(t =>
            t.HasCheckConstraint("CK_RiskEvaluation_Score", "\"TotalScore\" >= 0 AND \"TotalScore\" <= 100"));

        builder.Entity<Permission>().ToTable(t =>
            t.HasCheckConstraint("CK_Permission_Key_Shape", "position('.' in \"Key\") > 1"));

        builder.Entity<ApiKey>().ToTable(t =>
            t.HasCheckConstraint("CK_ApiKey_Hash_Length", "length(\"KeyHash\") = 64"));

        builder.Entity<AuditLog>().ToTable(t =>
            t.HasCheckConstraint("CK_AuditLog_Action_NotEmpty", "length(\"Action\") > 0"));

        builder.Entity<OrgEntities.Organization>().ToTable(t =>
            t.HasCheckConstraint("CK_Organization_Slug_NotEmpty", "length(\"Slug\") > 0"));
    }

    private static void ConfigureConcurrency(ModelBuilder builder)
    {
        // Optimistic concurrency via Postgres' xmin system column, on the
        // entities two people are most likely to edit at the same time.
        builder.Entity<Policy>().Property(p => p.RowVersion).IsRowVersion();
        builder.Entity<PolicyVersion>().Property(p => p.RowVersion).IsRowVersion();
        builder.Entity<AccessRequestEntities.AccessRequest>().Property(p => p.RowVersion).IsRowVersion();
        builder.Entity<Role>().Property(p => p.RowVersion).IsRowVersion();
        builder.Entity<ServiceAccount>().Property(p => p.RowVersion).IsRowVersion();
        builder.Entity<ApiKey>().Property(p => p.RowVersion).IsRowVersion();
        builder.Entity<CompliancePolicy>().Property(p => p.RowVersion).IsRowVersion();
        builder.Entity<Resource>().Property(p => p.RowVersion).IsRowVersion();
    }

    private void ConfigureTenantIsolation(ModelBuilder builder)
    {
        // Auditable + tenant owned: tenant filter AND soft-delete filter.
        ApplyTenantAndSoftDeleteFilter<OrgEntities.Department>(builder);
        ApplyTenantAndSoftDeleteFilter<Role>(builder);
        ApplyTenantAndSoftDeleteFilter<Permission>(builder);
        ApplyTenantAndSoftDeleteFilter<SoDConflictRule>(builder);
        ApplyTenantAndSoftDeleteFilter<Application>(builder);
        ApplyTenantAndSoftDeleteFilter<Resource>(builder);
        ApplyTenantAndSoftDeleteFilter<ResourceTypeDefinition>(builder);
        ApplyTenantAndSoftDeleteFilter<ResourceGroup>(builder);
        ApplyTenantAndSoftDeleteFilter<Policy>(builder);
        ApplyTenantAndSoftDeleteFilter<PolicyVersion>(builder);
        ApplyTenantAndSoftDeleteFilter<AccessRequestEntities.AccessRequest>(builder);
        ApplyTenantAndSoftDeleteFilter<TemporaryGrant>(builder);
        ApplyTenantAndSoftDeleteFilter<PrivilegedAccessRequest>(builder);
        ApplyTenantAndSoftDeleteFilter<AccessReviewCampaign>(builder);
        ApplyTenantAndSoftDeleteFilter<ServiceAccount>(builder);
        ApplyTenantAndSoftDeleteFilter<ApiKey>(builder);
        ApplyTenantAndSoftDeleteFilter<CompliancePolicy>(builder);

        // Tenant owned only (immutable/append-only rows, or join rows with no
        // lifecycle of their own).
        ApplyTenantFilter<UserRole>(builder);
        ApplyTenantFilter<AuditLog>(builder);
        ApplyTenantFilter<AuthorizationDecisionRecord>(builder);
        ApplyTenantFilter<SecurityEvent>(builder);
        ApplyTenantFilter<RiskEvaluation>(builder);
        ApplyTenantFilter<RiskSignalObservation>(builder);
        ApplyTenantFilter<PrivilegedSession>(builder);
        ApplyTenantFilter<NotificationRecord>(builder);
        ApplyTenantFilter<WebhookSubscription>(builder);
        ApplyTenantFilter<AccessReviewItem>(builder);
        ApplyTenantFilter<ComplianceFinding>(builder);
    }

    private void ApplyTenantFilter<TEntity>(ModelBuilder builder)
        where TEntity : class, ITenantOwned
        => builder.Entity<TEntity>().HasQueryFilter(e => e.OrganizationId == _tenant.OrganizationId);

    private void ApplyTenantAndSoftDeleteFilter<TEntity>(ModelBuilder builder)
        where TEntity : AuditableEntity, ITenantOwned
        => builder.Entity<TEntity>().HasQueryFilter(e => e.OrganizationId == _tenant.OrganizationId && !e.IsDeleted);
}
