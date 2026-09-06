using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.AccessRequest.Domain;
using Veritas.Web.Modules.Identity.Domain;
using Veritas.Web.Modules.Organization.Domain;
using Veritas.Web.Modules.PolicyManagement.Domain;
using Veritas.Web.Modules.ResourceManagement.Domain;
using Veritas.Web.Modules.RoleManagement.Domain;
using Veritas.Web.Shared.Infrastructure;

namespace Veritas.Web.Infrastructure.Seeding;

/// <summary>
/// Builds one believable enterprise tenant end to end — real rows written
/// through the actual DbSets, not JSON fixtures or SQL scripts that could
/// drift from the model. Every entity here follows the spec's naming
/// guidance (section 60): no Test1/Foo/Bar/Admin/User1, real-sounding org,
/// applications, departments, roles, and a policy that's actually Published
/// so /api/v1/authorize has something real to evaluate against out of the
/// box. Idempotent: re-running against a DB that already has this org's slug
/// is a no-op, so it's safe to call from Program.cs on every startup in
/// Development.
/// </summary>
public sealed class DemoDataSeeder
{
    private const string OrgSlug = "apex-financial-group";
    private readonly VeritasDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<DemoDataSeeder> _logger;

    public DemoDataSeeder(VeritasDbContext db, UserManager<ApplicationUser> userManager, ILogger<DemoDataSeeder> logger)
    {
        _db = db;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await _db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Slug == OrgSlug, ct))
        {
            _logger.LogInformation("Demo organization '{Slug}' already exists; skipping seed.", OrgSlug);
            return;
        }

        _logger.LogInformation("Seeding demo organization '{Slug}'...", OrgSlug);

        var org = new Organization { Name = "Apex Financial Group", Slug = OrgSlug };
        _db.Organizations.Add(org);

        var financeDept = new Department { OrganizationId = org.Id, Name = "Finance Technology" };
        var securityDept = new Department { OrganizationId = org.Id, Name = "Security" };
        var opsDept = new Department { OrganizationId = org.Id, Name = "Platform Operations" };
        _db.Departments.AddRange(financeDept, securityDept, opsDept);

        // --- Applications & resources -------------------------------------------------
        var paymentPlatform = new Application { OrganizationId = org.Id, Name = "Payment Platform", Owner = "Finance Technology", Environment = "production" };
        var riskAnalytics = new Application { OrganizationId = org.Id, Name = "Risk Analytics Platform", Owner = "Security", Environment = "production" };
        var corporateBanking = new Application { OrganizationId = org.Id, Name = "Corporate Banking Platform", Owner = "Platform Operations", Environment = "production" };
        _db.Applications.AddRange(paymentPlatform, riskAnalytics, corporateBanking);

        var paymentDb = new Resource
        {
            OrganizationId = org.Id, ApplicationId = paymentPlatform.Id, Name = "Production Payment Database",
            ResourceType = "database", Classification = "HIGHLY_CONFIDENTIAL", OwnerDepartment = "Finance Technology", Environment = "production"
        };
        var riskDashboard = new Resource
        {
            OrganizationId = org.Id, ApplicationId = riskAnalytics.Id, Name = "Risk Analytics Dashboard",
            ResourceType = "application", Classification = "CONFIDENTIAL", OwnerDepartment = "Security", Environment = "production"
        };
        var bankingLedger = new Resource
        {
            OrganizationId = org.Id, ApplicationId = corporateBanking.Id, Name = "Corporate Banking Ledger",
            ResourceType = "database", Classification = "HIGHLY_CONFIDENTIAL", OwnerDepartment = "Platform Operations", Environment = "production"
        };
        _db.Resources.AddRange(paymentDb, riskDashboard, bankingLedger);

        // --- Permissions ----------------------------------------------------------
        string[] permissionKeys =
        {
            "payment.read", "payment.create", "payment.approve", "payment.delete",
            "risk.read", "risk.manage",
            "banking.read", "banking.write",
            "audit.read", "access-review.manage"
        };
        var permissions = permissionKeys.Select(k => new Permission { OrganizationId = org.Id, Key = k, Description = $"Permission: {k}" }).ToList();
        _db.Permissions.AddRange(permissions);
        Permission Perm(string key) => permissions.First(p => p.Key == key);

        // --- Roles ------------------------------------------------------------------
        var financeManager = new Role { OrganizationId = org.Id, Name = "Finance Manager", Description = "Creates and reads payments" };
        var financeApprover = new Role { OrganizationId = org.Id, Name = "Payment Approver", Description = "Approves payments (kept separate from Finance Manager for SoD)" };
        var securityAdmin = new Role { OrganizationId = org.Id, Name = "Security Administrator", Description = "Manages policy, audit, and access reviews" };
        var auditor = new Role { OrganizationId = org.Id, Name = "Auditor", Description = "Read-only audit access" };
        _db.Roles2.AddRange(financeManager, financeApprover, securityAdmin, auditor);

        _db.RolePermissions.AddRange(
            new RolePermission { Role = financeManager, Permission = Perm("payment.read") },
            new RolePermission { Role = financeManager, Permission = Perm("payment.create") },
            new RolePermission { Role = financeApprover, Permission = Perm("payment.approve") },
            new RolePermission { Role = securityAdmin, Permission = Perm("audit.read") },
            new RolePermission { Role = securityAdmin, Permission = Perm("access-review.manage") },
            new RolePermission { Role = securityAdmin, Permission = Perm("risk.manage") },
            new RolePermission { Role = auditor, Permission = Perm("audit.read") });

        // --- Separation of Duties: the same pairing the platform's own demo scenario uses ---
        _db.SoDConflictRules.Add(new SoDConflictRule
        {
            OrganizationId = org.Id,
            PermissionKeyA = "payment.create",
            PermissionKeyB = "payment.approve",
            Description = "A user who can create a payment must not also be able to approve it."
        });

        // --- Users --------------------------------------------------------------
        var users = new List<(ApplicationUser User, string Password, Role[] Roles)>
        {
            (MakeUser(org.Id, financeDept.Id, "Sarah Khan", "sarah.khan@apexfinancial.example"), "CorrectHorse!Battery1", new[] { financeManager }),
            (MakeUser(org.Id, financeDept.Id, "John Smith", "john.smith@apexfinancial.example"), "CorrectHorse!Battery2", new[] { financeApprover }),
            (MakeUser(org.Id, securityDept.Id, "Priya Natarajan", "priya.natarajan@apexfinancial.example"), "CorrectHorse!Battery3", new[] { securityAdmin }),
            (MakeUser(org.Id, opsDept.Id, "Marcus Webb", "marcus.webb@apexfinancial.example"), "CorrectHorse!Battery4", new[] { auditor }),
        };

        foreach (var (user, password, roles) in users)
        {
            var createResult = await _userManager.CreateAsync(user, password);
            if (!createResult.Succeeded)
            {
                _logger.LogWarning("Could not create seed user {Email}: {Errors}", user.Email,
                    string.Join("; ", createResult.Errors.Select(e => e.Description)));
                continue;
            }

            foreach (var role in roles)
                _db.UserRoles2.Add(new UserRole { OrganizationId = org.Id, UserId = user.Id, RoleId = role.Id });
        }

        // --- A real, Published policy so /api/v1/authorize has something to evaluate ---
        var policy = new Policy { OrganizationId = org.Id, Name = "Production Payment Database Access", Owner = "Security" };
        _db.Policies.Add(policy);

        var version = new PolicyVersion
        {
            OrganizationId = org.Id,
            PolicyId = policy.Id,
            VersionNumber = 1,
            Status = PolicyLifecycleStatus.Published,
            PublishedAtUtc = DateTimeOffset.UtcNow
        };

        // Rule 1 (highest priority): deny highly-confidential resources without high device trust.
        var denyLowTrust = new PolicyRule { PolicyVersionId = version.Id, Priority = 1, Effect = PolicyEffect.Deny };
        denyLowTrust.Conditions.Add(new PolicyCondition { PolicyRuleId = denyLowTrust.Id, Attribute = "resource.classification", Operator = ConditionOperator.Equals, Value = "HIGHLY_CONFIDENTIAL" });
        denyLowTrust.Conditions.Add(new PolicyCondition { PolicyRuleId = denyLowTrust.Id, Attribute = "request.deviceTrust", Operator = ConditionOperator.NotEquals, Value = "HIGH" });

        // Rule 2: destructive actions in production require approval.
        var requireApproval = new PolicyRule { PolicyVersionId = version.Id, Priority = 2, Effect = PolicyEffect.RequireApproval };
        requireApproval.Conditions.Add(new PolicyCondition { PolicyRuleId = requireApproval.Id, Attribute = "request.action", Operator = ConditionOperator.Equals, Value = "delete" });
        requireApproval.Conditions.Add(new PolicyCondition { PolicyRuleId = requireApproval.Id, Attribute = "resource.environment", Operator = ConditionOperator.Equals, Value = "production" });

        // Rule 3: same-department active users on high-trust devices are allowed to read.
        var allowSameDept = new PolicyRule { PolicyVersionId = version.Id, Priority = 3, Effect = PolicyEffect.Allow };
        allowSameDept.Conditions.Add(new PolicyCondition { PolicyRuleId = allowSameDept.Id, Attribute = "user.status", Operator = ConditionOperator.Equals, Value = "ACTIVE" });
        allowSameDept.Conditions.Add(new PolicyCondition { PolicyRuleId = allowSameDept.Id, Attribute = "request.deviceTrust", Operator = ConditionOperator.Equals, Value = "HIGH" });
        allowSameDept.Conditions.Add(new PolicyCondition { PolicyRuleId = allowSameDept.Id, Attribute = "request.action", Operator = ConditionOperator.Equals, Value = "read" });

        version.Rules.AddRange(new[] { denyLowTrust, requireApproval, allowSameDept });
        policy.Versions.Add(version);
        _db.PolicyVersions.Add(version);

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded demo organization '{Slug}' with {UserCount} users, {AppCount} applications, and a Published policy.",
            OrgSlug, users.Count, 3);
    }

    private static ApplicationUser MakeUser(Guid orgId, Guid departmentId, string displayName, string email) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = orgId,
        DepartmentId = departmentId,
        UserName = email,
        Email = email,
        DisplayName = displayName,
        LifecycleState = UserLifecycleState.Active,
        EmailConfirmed = true
    };
}
