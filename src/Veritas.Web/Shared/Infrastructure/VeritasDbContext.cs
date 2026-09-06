using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.AccessReview.Domain;
using Veritas.Web.Modules.ApplicationRegistry.Domain;
using Veritas.Web.Modules.Audit.Domain;
using Veritas.Web.Modules.Identity.Domain;
using Veritas.Web.Modules.Notification.Domain;
using Veritas.Web.Modules.PolicyManagement.Domain;
using Veritas.Web.Modules.PrivilegedAccess.Domain;
using Veritas.Web.Modules.ResourceManagement.Domain;
using Veritas.Web.Modules.RiskManagement.Domain;
using Veritas.Web.Modules.RoleManagement.Domain;
using OrgEntities = Veritas.Web.Modules.Organization.Domain;
using AccessRequestEntities = Veritas.Web.Modules.AccessRequest.Domain;
using Veritas.Web.Shared.Domain;

namespace Veritas.Web.Shared.Infrastructure;

public class VeritasDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    private readonly ITenantContext _tenant;

    public VeritasDbContext(DbContextOptions<VeritasDbContext> options, ITenantContext tenant)
        : base(options)
    {
        _tenant = tenant;
    }

    public DbSet<OrgEntities.Organization> Organizations => Set<OrgEntities.Organization>();
    public DbSet<OrgEntities.Department> Departments => Set<OrgEntities.Department>();

    public DbSet<Role> Roles2 => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles2 => Set<UserRole>();
    public DbSet<SoDConflictRule> SoDConflictRules => Set<SoDConflictRule>();

    public DbSet<Application> Applications => Set<Application>();
    public DbSet<Resource> Resources => Set<Resource>();

    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<PolicyVersion> PolicyVersions => Set<PolicyVersion>();
    public DbSet<PolicyRule> PolicyRules => Set<PolicyRule>();
    public DbSet<PolicyCondition> PolicyConditions => Set<PolicyCondition>();

    public DbSet<AccessRequestEntities.AccessRequest> AccessRequests => Set<AccessRequestEntities.AccessRequest>();
    public DbSet<AccessRequestEntities.ApprovalStep> ApprovalSteps => Set<AccessRequestEntities.ApprovalStep>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AuthorizationDecisionRecord> AuthorizationDecisions => Set<AuthorizationDecisionRecord>();

    public DbSet<RiskEvaluation> RiskEvaluations => Set<RiskEvaluation>();
    public DbSet<RiskSignalObservation> RiskSignalObservations => Set<RiskSignalObservation>();

    public DbSet<TemporaryGrant> TemporaryGrants => Set<TemporaryGrant>();
    public DbSet<PrivilegedAccessRequest> PrivilegedAccessRequests => Set<PrivilegedAccessRequest>();
    public DbSet<PrivilegedSession> PrivilegedSessions => Set<PrivilegedSession>();

    public DbSet<NotificationRecord> Notifications => Set<NotificationRecord>();
    public DbSet<Veritas.Web.Modules.Notification.Domain.WebhookSubscription> WebhookSubscriptions => Set<Veritas.Web.Modules.Notification.Domain.WebhookSubscription>();

    public DbSet<AccessReviewCampaign> AccessReviewCampaigns => Set<AccessReviewCampaign>();
    public DbSet<AccessReviewItem> AccessReviewItems => Set<AccessReviewItem>();

    public DbSet<ServiceAccount> ServiceAccounts => Set<ServiceAccount>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<RolePermission>().HasKey(rp => new { rp.RoleId, rp.PermissionId });
        builder.Entity<UserRole>().HasKey(ur => new { ur.UserId, ur.RoleId });

        builder.Entity<Permission>()
            .HasIndex(p => new { p.OrganizationId, p.Key })
            .IsUnique();

        builder.Entity<PolicyVersion>()
            .HasIndex(pv => new { pv.PolicyId, pv.VersionNumber })
            .IsUnique();

        builder.Entity<OrgEntities.Organization>()
            .HasIndex(o => o.Slug)
            .IsUnique();

        builder.Entity<ApiKey>()
            .HasIndex(k => k.KeyHash)
            .IsUnique();

        // Optimistic concurrency via Postgres system column, on entities most
        // likely to be edited by two administrators/approvers concurrently.
        builder.Entity<Policy>().Property(p => p.RowVersion).IsRowVersion();
        builder.Entity<PolicyVersion>().Property(p => p.RowVersion).IsRowVersion();
        builder.Entity<AccessRequestEntities.AccessRequest>().Property(p => p.RowVersion).IsRowVersion();
        builder.Entity<Role>().Property(p => p.RowVersion).IsRowVersion();
        builder.Entity<ServiceAccount>().Property(p => p.RowVersion).IsRowVersion();
        builder.Entity<ApiKey>().Property(p => p.RowVersion).IsRowVersion();

        // --- Tenant isolation: global query filters on every ITenantOwned entity ---
        // This makes cross-tenant reads impossible through the normal DbContext API,
        // per section 6 of the spec. Writes are still validated in application services.
        ApplyTenantFilter<OrgEntities.Department>(builder);
        ApplyTenantFilter<Role>(builder);
        ApplyTenantFilter<Permission>(builder);
        ApplyTenantFilter<SoDConflictRule>(builder);
        ApplyTenantFilter<UserRole>(builder);
        ApplyTenantFilter<Application>(builder);
        ApplyTenantFilter<Resource>(builder);
        ApplyTenantFilter<Policy>(builder);
        ApplyTenantFilter<PolicyVersion>(builder);
        ApplyTenantFilter<AccessRequestEntities.AccessRequest>(builder);
        ApplyTenantFilter<AuditLog>(builder);
        ApplyTenantFilter<AuthorizationDecisionRecord>(builder);
        ApplyTenantFilter<RiskEvaluation>(builder);
        ApplyTenantFilter<RiskSignalObservation>(builder);
        ApplyTenantFilter<TemporaryGrant>(builder);
        ApplyTenantFilter<PrivilegedAccessRequest>(builder);
        ApplyTenantFilter<PrivilegedSession>(builder);
        ApplyTenantFilter<NotificationRecord>(builder);
        ApplyTenantFilter<Veritas.Web.Modules.Notification.Domain.WebhookSubscription>(builder);
        ApplyTenantFilter<AccessReviewCampaign>(builder);
        ApplyTenantFilter<AccessReviewItem>(builder);
        ApplyTenantFilter<ServiceAccount>(builder);
        ApplyTenantFilter<ApiKey>(builder);
    }

    private void ApplyTenantFilter<TEntity>(ModelBuilder builder) where TEntity : class, ITenantOwned
    {
        builder.Entity<TEntity>()
            .HasQueryFilter(e => e.OrganizationId == _tenant.OrganizationId);
    }
}
