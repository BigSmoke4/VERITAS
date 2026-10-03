using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Veritas.Web.Modules.AccessRequest.Domain;
using Veritas.Web.Modules.ApplicationRegistry.Domain;
using Veritas.Web.Modules.PrivilegedAccess.Domain;
using Veritas.Web.Modules.Audit.Domain;
using Veritas.Web.Modules.Compliance.Domain;
using Veritas.Web.Modules.Identity.Domain;
using Veritas.Web.Modules.Notification.Domain;
using Veritas.Web.Modules.PolicyManagement.Domain;
using Veritas.Web.Modules.ResourceManagement.Domain;
using Veritas.Web.Modules.RiskManagement.Domain;
using Veritas.Web.Modules.RoleManagement.Domain;
using Veritas.Web.Shared.Infrastructure;
using OrgEntities = Veritas.Web.Modules.Organization.Domain;

namespace Veritas.Web.Infrastructure.Seeding;

/// <summary>
/// Builds one believable enterprise tenant end to end (spec section 60): real
/// rows written through the actual DbSets, not JSON fixtures or SQL scripts
/// that could drift from the model.
///
/// It is deliberately arranged so the three documented demonstration scenarios
/// (spec sections 73-75) can be run against a freshly created database:
///   * John Smith holds NO static payment.read, so "Temporary Production Payment
///     Database Access" must go through request -&gt; approval -&gt; JIT grant.
///   * Sarah Khan holds payment.create, so requesting payment.approve trips the
///     Separation-of-Duties rule.
///   * A second draft policy version exists so the Policy Simulator has a real
///     before/after to compare.
///
/// Idempotent: re-running against a database that already contains the
/// organization slug is a no-op.
/// </summary>
public sealed class DemoDataSeeder
{
    public const string OrgSlug = "apex-financial-group";
    private const string DemoIp = "10.20.10.12";

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

        var org = new OrgEntities.Organization { Name = "Apex Financial Group", Slug = OrgSlug };
        _db.Organizations.Add(org);

        // --- Departments ------------------------------------------------------
        var financeDept = new OrgEntities.Department { OrganizationId = org.Id, Name = "Finance Technology" };
        var securityDept = new OrgEntities.Department { OrganizationId = org.Id, Name = "Security" };
        var opsDept = new OrgEntities.Department { OrganizationId = org.Id, Name = "Platform Operations" };
        var customerOps = new OrgEntities.Department { OrganizationId = org.Id, Name = "Customer Operations" };
        var dataAnalytics = new OrgEntities.Department { OrganizationId = org.Id, Name = "Data & Analytics" };
        _db.Departments.AddRange(financeDept, securityDept, opsDept, customerOps, dataAnalytics);

        // --- Applications -----------------------------------------------------
        var paymentPlatform = new Application { OrganizationId = org.Id, Name = "Payment Platform", Owner = "Finance Technology", Environment = "production", Status = "ACTIVE" };
        var customerIdentity = new Application { OrganizationId = org.Id, Name = "Customer Identity Platform", Owner = "Customer Operations", Environment = "production", Status = "ACTIVE" };
        var riskAnalytics = new Application { OrganizationId = org.Id, Name = "Risk Analytics Platform", Owner = "Security", Environment = "production", Status = "ACTIVE" };
        var corporateBanking = new Application { OrganizationId = org.Id, Name = "Corporate Banking Platform", Owner = "Platform Operations", Environment = "production", Status = "ACTIVE" };
        var notificationPlatform = new Application { OrganizationId = org.Id, Name = "Notification Platform", Owner = "Platform Operations", Environment = "staging", Status = "ACTIVE" };
        _db.Applications.AddRange(paymentPlatform, customerIdentity, riskAnalytics, corporateBanking, notificationPlatform);

        // --- Resource types ---------------------------------------------------
        _db.ResourceTypes.AddRange(
            new ResourceTypeDefinition { OrganizationId = org.Id, Key = "database", Description = "Relational or analytical data store", DefaultClassification = "CONFIDENTIAL" },
            new ResourceTypeDefinition { OrganizationId = org.Id, Key = "api", Description = "Internal or partner-facing HTTP API", DefaultClassification = "INTERNAL" },
            new ResourceTypeDefinition { OrganizationId = org.Id, Key = "application", Description = "Operator-facing application", DefaultClassification = "INTERNAL" },
            new ResourceTypeDefinition { OrganizationId = org.Id, Key = "queue", Description = "Message broker topic or queue", DefaultClassification = "INTERNAL" },
            new ResourceTypeDefinition { OrganizationId = org.Id, Key = "host", Description = "Compute host or container fleet", DefaultClassification = "CONFIDENTIAL" });

        // --- Resources (PermissionKeyPrefix drives the resource.action key) ----
        var paymentDb = new Resource
        {
            OrganizationId = org.Id, ApplicationId = paymentPlatform.Id, Name = "Production Payment Database",
            ResourceType = "database", Classification = "HIGHLY_CONFIDENTIAL", OwnerDepartment = "Finance Technology",
            Environment = "production", PermissionKeyPrefix = "payment"
        };
        var paymentApi = new Resource
        {
            OrganizationId = org.Id, ApplicationId = paymentPlatform.Id, Name = "Payment Authorization API",
            ResourceType = "api", Classification = "CONFIDENTIAL", OwnerDepartment = "Finance Technology",
            Environment = "production", PermissionKeyPrefix = "payment"
        };
        var customerStore = new Resource
        {
            OrganizationId = org.Id, ApplicationId = customerIdentity.Id, Name = "Customer Identity Store",
            ResourceType = "database", Classification = "HIGHLY_CONFIDENTIAL", OwnerDepartment = "Customer Operations",
            Environment = "production", PermissionKeyPrefix = "customer"
        };
        var riskDashboard = new Resource
        {
            OrganizationId = org.Id, ApplicationId = riskAnalytics.Id, Name = "Risk Analytics Dashboard",
            ResourceType = "application", Classification = "CONFIDENTIAL", OwnerDepartment = "Security",
            Environment = "production", PermissionKeyPrefix = "risk"
        };
        var bankingLedger = new Resource
        {
            OrganizationId = org.Id, ApplicationId = corporateBanking.Id, Name = "Corporate Banking Ledger",
            ResourceType = "database", Classification = "HIGHLY_CONFIDENTIAL", OwnerDepartment = "Platform Operations",
            Environment = "production", PermissionKeyPrefix = "banking"
        };
        var incidentLogStore = new Resource
        {
            OrganizationId = org.Id, ApplicationId = corporateBanking.Id, Name = "Production Incident Log Store",
            ResourceType = "database", Classification = "CONFIDENTIAL", OwnerDepartment = "Platform Operations",
            Environment = "production", PermissionKeyPrefix = "production.logs"
        };
        var notificationQueue = new Resource
        {
            OrganizationId = org.Id, ApplicationId = notificationPlatform.Id, Name = "Outbound Notification Queue",
            ResourceType = "queue", Classification = "INTERNAL", OwnerDepartment = "Platform Operations",
            Environment = "staging", PermissionKeyPrefix = "notification"
        };
        var prodSshFleet = new Resource
        {
            OrganizationId = org.Id, ApplicationId = corporateBanking.Id, Name = "Production Server SSH Fleet",
            ResourceType = "host", Classification = "HIGHLY_CONFIDENTIAL", OwnerDepartment = "Platform Operations",
            Environment = "production", PermissionKeyPrefix = "production.server"
        };
        _db.Resources.AddRange(paymentDb, paymentApi, customerStore, riskDashboard, bankingLedger, incidentLogStore, notificationQueue, prodSshFleet);

        // --- Permissions ------------------------------------------------------
        string[] permissionKeys =
        {
            "payment.read", "payment.create", "payment.approve", "payment.delete",
            "customer.read", "customer.modify", "customer.export",
            "risk.read", "risk.manage",
            "banking.read", "banking.write", "banking.reconcile",
            "incident.read", "incident.manage",
            "production.logs.read", "production.server.ssh",
            "notification.send",
            "audit.read", "access-review.manage", "policy.manage"
        };
        var permissions = permissionKeys
            .Select(k => new Permission { OrganizationId = org.Id, Key = k, Description = $"Grants the '{k}' capability." })
            .ToList();
        _db.Permissions.AddRange(permissions);
        Permission Perm(string key) => permissions.First(p => p.Key == key);

        // --- Roles ------------------------------------------------------------
        var financeManager = new Role { OrganizationId = org.Id, Name = "Finance Manager", Description = "Creates and reads payments", IsSystemRole = false };
        var paymentApprover = new Role { OrganizationId = org.Id, Name = "Payment Approver", Description = "Approves payments (deliberately separate from Finance Manager for SoD)" };
        var incidentResponder = new Role { OrganizationId = org.Id, Name = "Incident Responder", Description = "Read-only incident and production log access" };
        var securityAdmin = new Role { OrganizationId = org.Id, Name = "Security Administrator", Description = "Manages policy, audit and access reviews", IsSystemRole = true };
        var securityAnalyst = new Role { OrganizationId = org.Id, Name = "Security Analyst", Description = "Reads audit and risk data" };
        var releaseManager = new Role { OrganizationId = org.Id, Name = "Release Manager", Description = "Coordinates production releases" };
        var auditor = new Role { OrganizationId = org.Id, Name = "Auditor", Description = "Read-only audit access" };
        var viewer = new Role { OrganizationId = org.Id, Name = "Viewer", Description = "Read-only access to non-sensitive dashboards" };
        _db.Roles2.AddRange(financeManager, paymentApprover, incidentResponder, securityAdmin, securityAnalyst, releaseManager, auditor, viewer);

        _db.RolePermissions.AddRange(
            new RolePermission { Role = financeManager, Permission = Perm("payment.read") },
            new RolePermission { Role = financeManager, Permission = Perm("payment.create") },
            new RolePermission { Role = paymentApprover, Permission = Perm("payment.approve") },
            new RolePermission { Role = paymentApprover, Permission = Perm("payment.read") },
            new RolePermission { Role = incidentResponder, Permission = Perm("incident.read") },
            new RolePermission { Role = incidentResponder, Permission = Perm("production.logs.read") },
            new RolePermission { Role = securityAdmin, Permission = Perm("audit.read") },
            new RolePermission { Role = securityAdmin, Permission = Perm("access-review.manage") },
            new RolePermission { Role = securityAdmin, Permission = Perm("policy.manage") },
            new RolePermission { Role = securityAdmin, Permission = Perm("risk.manage") },
            new RolePermission { Role = securityAnalyst, Permission = Perm("audit.read") },
            new RolePermission { Role = securityAnalyst, Permission = Perm("risk.read") },
            new RolePermission { Role = releaseManager, Permission = Perm("incident.manage") },
            new RolePermission { Role = releaseManager, Permission = Perm("notification.send") },
            new RolePermission { Role = auditor, Permission = Perm("audit.read") },
            new RolePermission { Role = viewer, Permission = Perm("risk.read") });

        // --- Separation of Duties ---------------------------------------------
        _db.SoDConflictRules.AddRange(
            new SoDConflictRule
            {
                OrganizationId = org.Id,
                PermissionKeyA = "payment.create",
                PermissionKeyB = "payment.approve",
                Description = "A user who can create a payment must not also be able to approve it."
            },
            new SoDConflictRule
            {
                OrganizationId = org.Id,
                PermissionKeyA = "customer.modify",
                PermissionKeyB = "audit.read",
                Description = "Operators who can modify customer records must not also administer the audit trail."
            });

        // --- Users ------------------------------------------------------------
        var users = new List<(ApplicationUser User, string Password, Role[] Roles)>
        {
            (MakeUser(org.Id, financeDept.Id, "Sarah Khan", "sarah.khan@apexfinancial.example", "Engineering Manager", "HIGHLY_CONFIDENTIAL", "Dhaka", "BD"), "CorrectHorse!Battery1", new[] { financeManager }),
            // John Smith deliberately holds no payment.read: the JIT demo requires him to request it.
            (MakeUser(org.Id, financeDept.Id, "John Smith", "john.smith@apexfinancial.example", "Payments Analyst", "CONFIDENTIAL", "Dhaka", "BD"), "CorrectHorse!Battery2", new[] { incidentResponder }),
            (MakeUser(org.Id, securityDept.Id, "Priya Natarajan", "priya.natarajan@apexfinancial.example", "Security Architect", "HIGHLY_CONFIDENTIAL", "Singapore", "SG"), "CorrectHorse!Battery3", new[] { securityAdmin }),
            (MakeUser(org.Id, securityDept.Id, "Daniel Osei", "daniel.osei@apexfinancial.example", "Security Analyst", "CONFIDENTIAL", "London", "GB"), "CorrectHorse!Battery4", new[] { securityAnalyst }),
            (MakeUser(org.Id, opsDept.Id, "Marcus Webb", "marcus.webb@apexfinancial.example", "Platform Lead", "HIGHLY_CONFIDENTIAL", "London", "GB"), "CorrectHorse!Battery5", new[] { releaseManager }),
            (MakeUser(org.Id, opsDept.Id, "Elena Fischer", "elena.fischer@apexfinancial.example", "Site Reliability Engineer", "CONFIDENTIAL", "Frankfurt", "DE"), "CorrectHorse!Battery6", new[] { releaseManager }),
            (MakeUser(org.Id, customerOps.Id, "Aisha Rahman", "aisha.rahman@apexfinancial.example", "Customer Operations Lead", "CONFIDENTIAL", "Dhaka", "BD"), "CorrectHorse!Battery7", new[] { viewer }),
            (MakeUser(org.Id, dataAnalytics.Id, "Tomás Alvarez", "tomas.alvarez@apexfinancial.example", "Data Engineer", "CONFIDENTIAL", "Madrid", "ES"), "CorrectHorse!Battery8", new[] { viewer }),
            (MakeUser(org.Id, securityDept.Id, "Grace Liu", "grace.liu@apexfinancial.example", "Internal Auditor", "CONFIDENTIAL", "Toronto", "CA"), "CorrectHorse!Battery9", new[] { auditor }),
            (MakeUser(org.Id, financeDept.Id, "Robert Mbeki", "robert.mbeki@apexfinancial.example", "Payment Approver", "HIGHLY_CONFIDENTIAL", "Johannesburg", "ZA"), "CorrectHorse!Battery10", new[] { paymentApprover }),
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

        // --- Risk signal observations -----------------------------------------
        // These make risk scoring deterministic and explainable for the demo:
        // the demo IP is a known location, so "new device" is not triggered.
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in users)
        {
            var observedUser = entry.User;
            _db.RiskSignalObservations.AddRange(
                new RiskSignalObservation { OrganizationId = org.Id, UserId = observedUser.Id, SignalType = "KNOWN_IP", Value = DemoIp, ObservedAtUtc = now.AddDays(-3) },
                new RiskSignalObservation { OrganizationId = org.Id, UserId = observedUser.Id, SignalType = "LOGIN_LOCATION", Value = DemoIp, ObservedAtUtc = now.AddDays(-1) });
        }

        // --- Service accounts -------------------------------------------------
        _db.ServiceAccounts.AddRange(
            new ServiceAccount { OrganizationId = org.Id, ApplicationId = paymentPlatform.Id, Name = "PaymentService", Owner = "Finance Technology", Status = "ACTIVE", LastUsedAtUtc = now.AddMinutes(-4) },
            new ServiceAccount { OrganizationId = org.Id, ApplicationId = notificationPlatform.Id, Name = "NotificationService", Owner = "Platform Operations", Status = "ACTIVE", LastUsedAtUtc = now.AddMinutes(-11) },
            new ServiceAccount { OrganizationId = org.Id, ApplicationId = riskAnalytics.Id, Name = "RiskEngine", Owner = "Security", Status = "ACTIVE", LastUsedAtUtc = now.AddHours(-2) },
            new ServiceAccount { OrganizationId = org.Id, ApplicationId = corporateBanking.Id, Name = "ReportingService", Owner = "Platform Operations", Status = "DISABLED", LastUsedAtUtc = now.AddDays(-41) });

        // --- Published policy (the one /api/v1/authorize evaluates) -----------
        var paymentPolicy = new Policy { OrganizationId = org.Id, Name = "Production Payment Database Access", Owner = "Security" };
        _db.Policies.Add(paymentPolicy);

        var paymentV1 = new PolicyVersion
        {
            OrganizationId = org.Id,
            PolicyId = paymentPolicy.Id,
            VersionNumber = 1,
            Status = PolicyLifecycleStatus.Published,
            PublishedAtUtc = now.AddDays(-12)
        };

        var denyLowTrust = new PolicyRule { PolicyVersionId = paymentV1.Id, Priority = 1, Effect = PolicyEffect.Deny };
        denyLowTrust.Conditions.Add(new PolicyCondition { PolicyRuleId = denyLowTrust.Id, Attribute = "resource.classification", Operator = ConditionOperator.Equals, Value = "HIGHLY_CONFIDENTIAL" });
        denyLowTrust.Conditions.Add(new PolicyCondition { PolicyRuleId = denyLowTrust.Id, Attribute = "request.deviceTrust", Operator = ConditionOperator.NotEquals, Value = "HIGH" });

        var requireApprovalForDelete = new PolicyRule { PolicyVersionId = paymentV1.Id, Priority = 2, Effect = PolicyEffect.RequireApproval };
        requireApprovalForDelete.Conditions.Add(new PolicyCondition { PolicyRuleId = requireApprovalForDelete.Id, Attribute = "request.action", Operator = ConditionOperator.Equals, Value = "delete" });
        requireApprovalForDelete.Conditions.Add(new PolicyCondition { PolicyRuleId = requireApprovalForDelete.Id, Attribute = "resource.environment", Operator = ConditionOperator.Equals, Value = "production" });

        var allowRead = new PolicyRule { PolicyVersionId = paymentV1.Id, Priority = 3, Effect = PolicyEffect.Allow };
        allowRead.Conditions.Add(new PolicyCondition { PolicyRuleId = allowRead.Id, Attribute = "user.status", Operator = ConditionOperator.Equals, Value = "ACTIVE" });
        allowRead.Conditions.Add(new PolicyCondition { PolicyRuleId = allowRead.Id, Attribute = "request.deviceTrust", Operator = ConditionOperator.Equals, Value = "HIGH" });
        allowRead.Conditions.Add(new PolicyCondition { PolicyRuleId = allowRead.Id, Attribute = "request.action", Operator = ConditionOperator.Equals, Value = "read" });
        allowRead.Conditions.Add(new PolicyCondition { PolicyRuleId = allowRead.Id, Attribute = "request.authenticationStrength", Operator = ConditionOperator.GreaterThanOrEqual, Value = "STRONG" });

        paymentV1.Rules.AddRange(new[] { denyLowTrust, requireApprovalForDelete, allowRead });
        paymentPolicy.Versions.Add(paymentV1);
        _db.PolicyVersions.Add(paymentV1);

        // A second policy with a Published version and a Draft v2, so the Policy
        // Simulator has a genuine before/after comparison to run.
        var prodDbPolicy = new Policy { OrganizationId = org.Id, Name = "Production Database Access", Owner = "Security" };
        _db.Policies.Add(prodDbPolicy);

        var prodV1 = new PolicyVersion
        {
            OrganizationId = org.Id, PolicyId = prodDbPolicy.Id, VersionNumber = 1,
            Status = PolicyLifecycleStatus.Published, PublishedAtUtc = now.AddDays(-30)
        };
        var prodAllowSameDept = new PolicyRule { PolicyVersionId = prodV1.Id, Priority = 1, Effect = PolicyEffect.Allow };
        prodAllowSameDept.Conditions.Add(new PolicyCondition { PolicyRuleId = prodAllowSameDept.Id, Attribute = "user.department", Operator = ConditionOperator.Equals, Value = "resource.department", IsValueAttributeRef = true });
        prodAllowSameDept.Conditions.Add(new PolicyCondition { PolicyRuleId = prodAllowSameDept.Id, Attribute = "user.status", Operator = ConditionOperator.Equals, Value = "ACTIVE" });
        prodV1.Rules.Add(prodAllowSameDept);
        _db.PolicyVersions.Add(prodV1);

        var prodV2 = new PolicyVersion
        {
            OrganizationId = org.Id, PolicyId = prodDbPolicy.Id, VersionNumber = 2,
            Status = PolicyLifecycleStatus.Draft
        };
        var prodV2Allow = new PolicyRule { PolicyVersionId = prodV2.Id, Priority = 1, Effect = PolicyEffect.Allow };
        prodV2Allow.Conditions.Add(new PolicyCondition { PolicyRuleId = prodV2Allow.Id, Attribute = "user.department", Operator = ConditionOperator.Equals, Value = "resource.department", IsValueAttributeRef = true });
        prodV2Allow.Conditions.Add(new PolicyCondition { PolicyRuleId = prodV2Allow.Id, Attribute = "user.status", Operator = ConditionOperator.Equals, Value = "ACTIVE" });
        prodV2Allow.Conditions.Add(new PolicyCondition { PolicyRuleId = prodV2Allow.Id, Attribute = "request.authenticationStrength", Operator = ConditionOperator.Equals, Value = "STRONG" });
        var prodV2Deny = new PolicyRule { PolicyVersionId = prodV2.Id, Priority = 2, Effect = PolicyEffect.Deny };
        prodV2Deny.Conditions.Add(new PolicyCondition { PolicyRuleId = prodV2Deny.Id, Attribute = "resource.classification", Operator = ConditionOperator.Equals, Value = "HIGHLY_CONFIDENTIAL" });
        prodV2Deny.Conditions.Add(new PolicyCondition { PolicyRuleId = prodV2Deny.Id, Attribute = "request.deviceTrust", Operator = ConditionOperator.NotEquals, Value = "HIGH" });
        prodV2.Rules.AddRange(new[] { prodV2Allow, prodV2Deny });
        _db.PolicyVersions.Add(prodV2);

        // --- A granted access request with a live temporary grant -------------
        var marcus = users[4].User;
        var grantedRequest = new AccessRequest
        {
            OrganizationId = org.Id,
            RequestedByUserId = marcus.Id,
            ResourceId = incidentLogStore.Id,
            PermissionKey = "production.logs.read",
            Duration = TimeSpan.FromHours(4),
            BusinessJustification = "Investigate reconciliation incident INC-20482",
            Status = AccessRequestStatus.Granted,
            GrantedAtUtc = now.AddMinutes(-20),
            ExpiresAtUtc = now.AddHours(4).AddMinutes(-20)
        };
        grantedRequest.Steps.Add(new ApprovalStep { AccessRequestId = grantedRequest.Id, Order = 1, ApproverRole = "Manager", Decision = "APPROVED", DecidedByUserId = users[0].User.Id, DecidedAtUtc = now.AddMinutes(-30), Comment = "Approved for the active incident." });
        grantedRequest.Steps.Add(new ApprovalStep { AccessRequestId = grantedRequest.Id, Order = 2, ApproverRole = "ResourceOwner", Decision = "APPROVED", DecidedByUserId = users[5].User.Id, DecidedAtUtc = now.AddMinutes(-25), Comment = "Resource owner sign-off." });
        _db.AccessRequests.Add(grantedRequest);

        _db.TemporaryGrants.Add(new TemporaryGrant
        {
            OrganizationId = org.Id,
            UserId = marcus.Id,
            ResourceId = incidentLogStore.Id,
            PermissionKey = "production.logs.read",
            Reason = "Access request: investigate reconciliation incident INC-20482",
            StartAtUtc = now.AddMinutes(-20),
            ExpiresAtUtc = now.AddHours(4).AddMinutes(-20)
        });

        // A pending request so the approval queue is not empty on first run.
        var pendingRequest = new AccessRequest
        {
            OrganizationId = org.Id,
            RequestedByUserId = users[7].User.Id,
            ResourceId = riskDashboard.Id,
            PermissionKey = "risk.read",
            Duration = TimeSpan.FromDays(7),
            BusinessJustification = "Quarterly exposure modelling for the board pack",
            Status = AccessRequestStatus.Requested
        };
        pendingRequest.Steps.Add(new ApprovalStep { AccessRequestId = pendingRequest.Id, Order = 1, ApproverRole = "Manager" });
        pendingRequest.Steps.Add(new ApprovalStep { AccessRequestId = pendingRequest.Id, Order = 2, ApproverRole = "ResourceOwner" });
        _db.AccessRequests.Add(pendingRequest);

        // --- Compliance controls ----------------------------------------------
        _db.CompliancePolicies.AddRange(
            new CompliancePolicy { OrganizationId = org.Id, ControlType = ControlTypes.InactiveUser, Title = "No dormant active accounts", Description = "Active accounts must have logged in within the threshold.", Severity = "MEDIUM", Threshold = 90, Framework = "SOC 2 CC6.2", Enabled = true },
            new CompliancePolicy { OrganizationId = org.Id, ControlType = ControlTypes.DormantPrivilegedAccount, Title = "No dormant privileged accounts", Description = "Privileged accounts must exercise their access within the threshold.", Severity = "HIGH", Threshold = 30, Framework = "SOC 2 CC6.3", Enabled = true },
            new CompliancePolicy { OrganizationId = org.Id, ControlType = ControlTypes.UnusedPermission, Title = "No unused permissions", Description = "Permissions not granted by any role should be retired.", Severity = "LOW", Threshold = 0, Framework = "ISO 27001 A.9.2", Enabled = true },
            new CompliancePolicy { OrganizationId = org.Id, ControlType = ControlTypes.OrphanedAccount, Title = "No orphaned accounts", Description = "Every active account must resolve to an owning department.", Severity = "MEDIUM", Threshold = 0, Framework = "ISO 27001 A.9.2", Enabled = true },
            new CompliancePolicy { OrganizationId = org.Id, ControlType = ControlTypes.ExcessivePrivileges, Title = "Limit concurrent roles", Description = "No user should hold more roles than the threshold.", Severity = "MEDIUM", Threshold = 3, Framework = "SOC 2 CC6.1", Enabled = true },
            new CompliancePolicy { OrganizationId = org.Id, ControlType = ControlTypes.ExpiredTemporaryAccess, Title = "Temporary access must be revoked on expiry", Description = "Grants past their expiry must already be revoked.", Severity = "HIGH", Threshold = 0, Framework = "SOC 2 CC6.3", Enabled = true });

        // --- Notification subscription so webhook delivery has a real target ---
        _db.WebhookSubscriptions.Add(new WebhookSubscription
        {
            OrganizationId = org.Id,
            EventType = "*",
            TargetUrl = "https://example.invalid/veritas-webhook",
            Enabled = false
        });

        // --- Historical decisions/audit so dashboards are not empty ------------
        var random = new Random(20260101); // fixed seed: reproducible demo history, never presented as live telemetry
        var demoUsers = users.Select(u => u.User).ToList();
        var demoResources = new[] { paymentDb, paymentApi, riskDashboard, bankingLedger, incidentLogStore };

        for (var i = 0; i < 240; i++)
        {
            var user = demoUsers[random.Next(demoUsers.Count)];
            var resource = demoResources[random.Next(demoResources.Count)];
            var isAllow = random.Next(100) < 88;
            var evaluatedAt = now.AddDays(-random.Next(0, 30)).AddMinutes(-random.Next(0, 1440));
            var decisionId = Guid.NewGuid();

            _db.AuthorizationDecisions.Add(new AuthorizationDecisionRecord
            {
                Id = decisionId,
                OrganizationId = org.Id,
                SubjectUserId = user.Id.ToString(),
                ResourceId = resource.Id.ToString(),
                Action = isAllow ? "read" : "delete",
                Environment = resource.Environment,
                Result = isAllow ? "Allow" : "Deny",
                PolicyId = paymentPolicy.Id,
                PolicyVersionId = paymentV1.Id,
                RiskScore = isAllow ? random.Next(5, 45) : random.Next(45, 95),
                RiskLevel = isAllow ? "LOW" : "HIGH",
                RequiredPermissionKey = $"{resource.PermissionKeyPrefix}.{(isAllow ? "read" : "delete")}",
                ReasonsJson = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    isAllow ? "Matched rule 3 on policy version 1 -> Allow" : "Required permission missing."
                }),
                ChecksJson = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new { Code = "IDENTITY_VERIFIED", Description = "Subject identifier is well-formed", Passed = true, Detail = (string?)null },
                    new { Code = "PERMISSION_HELD", Description = "Subject holds the required permission", Passed = isAllow, Detail = (string?)null }
                }),
                ContextJson = "{\"Ip\":\"10.20.10.12\",\"DeviceTrust\":\"HIGH\"}",
                CorrelationId = Guid.NewGuid().ToString("N"),
                EvaluatedAtUtc = evaluatedAt
            });

            _db.AuditLogs.Add(new AuditLog
            {
                OrganizationId = org.Id,
                ActorUserId = user.Id,
                Action = isAllow ? "AUTHORIZATION_ALLOWED" : "AUTHORIZATION_DENIED",
                ResourceId = resource.Id.ToString(),
                NewValue = isAllow ? "Allow" : "Deny",
                DecisionId = decisionId,
                CorrelationId = Guid.NewGuid().ToString("N"),
                TimestampUtc = evaluatedAt,
                Ip = DemoIp
            });
        }

        _db.AuditLogs.AddRange(
            new AuditLog { OrganizationId = org.Id, ActorUserId = users[2].User.Id, Action = "POLICY_PUBLISHED", ResourceId = paymentPolicy.Id.ToString(), PreviousValue = "Approved", NewValue = "Published", CorrelationId = paymentV1.Id.ToString(), TimestampUtc = now.AddDays(-12), Ip = DemoIp },
            new AuditLog { OrganizationId = org.Id, ActorUserId = users[0].User.Id, Action = "ROLE_ASSIGNED", ResourceId = financeManager.Id.ToString(), NewValue = "Finance Manager", CorrelationId = users[0].User.Id.ToString(), TimestampUtc = now.AddDays(-40), Ip = DemoIp });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Seeded demo organization '{Slug}': {Users} users, {Apps} applications, {Resources} resources, {Permissions} permissions, {Policies} policies.",
            OrgSlug, users.Count, 5, 8, permissionKeys.Length, 2);
    }

    private static ApplicationUser MakeUser(
        Guid orgId, Guid departmentId, string displayName, string email,
        string jobTitle, string clearance, string location, string country) => new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            DepartmentId = departmentId,
            UserName = email,
            Email = email,
            DisplayName = displayName,
            JobTitle = jobTitle,
            Clearance = clearance,
            Location = location,
            Country = country,
            LifecycleState = UserLifecycleState.Active,
            EmailConfirmed = true,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-120),
            LastLoginAtUtc = DateTimeOffset.UtcNow.AddDays(-2)
        };
}
