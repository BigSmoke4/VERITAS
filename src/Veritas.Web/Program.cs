using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Context;
using StackExchange.Redis;
using Veritas.Web.BackgroundWorkers;
using Veritas.Web.Controllers;
using Veritas.Web.Infrastructure;
using Veritas.Web.Infrastructure.Caching;
using Veritas.Web.Infrastructure.Idempotency;
using Veritas.Web.Modules.AccessRequest.Application;
using Veritas.Web.Modules.AccessReview.Application;
using Veritas.Web.Modules.Administration.Application;
using Veritas.Web.Modules.Analytics.Application;
using Veritas.Web.Modules.ApplicationRegistry.Application;
using Veritas.Web.Modules.Approval.Application;
using Veritas.Web.Modules.Audit.Application;
using Veritas.Web.Modules.Authorization.Application;
using Veritas.Web.Modules.Compliance.Application;
using Veritas.Web.Modules.Identity.Application;
using Veritas.Web.Modules.Identity.Domain;
using Veritas.Web.Modules.Notification.Application;
using Veritas.Web.Modules.PermissionManagement.Application;
using Veritas.Web.Modules.PolicyManagement.Application;
using Veritas.Web.Modules.PrivilegedAccess.Application;
using Veritas.Web.Modules.PrivilegedAccess.Infrastructure;
using Veritas.Web.Modules.ResourceManagement.Application;
using Veritas.Web.Modules.RiskManagement.Application;
using Veritas.Web.Modules.RoleManagement.Application;
using Veritas.Web.Observability;
using Veritas.Web.Shared.Application.AccessGrants;
using Veritas.Web.Shared.Application.Configuration;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Structured logging (spec section 40). CorrelationId / TenantId / UserId are
// pushed into LogContext per request by the middleware below.
// ---------------------------------------------------------------------------
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Veritas")
    .WriteTo.Console());

// ---------------------------------------------------------------------------
// Strongly-typed options (spec section 67). Validated at startup so a
// misconfigured deployment fails fast instead of misbehaving at request time.
// ---------------------------------------------------------------------------
builder.Services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.SectionName)
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<RedisOptions>().BindConfiguration(RedisOptions.SectionName)
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SecurityOptions>().BindConfiguration(SecurityOptions.SectionName)
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<VeritasIdentityOptions>().BindConfiguration(VeritasIdentityOptions.SectionName)
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<RateLimitOptions>().BindConfiguration(RateLimitOptions.SectionName)
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<AuditOptions>().BindConfiguration(AuditOptions.SectionName)
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<NotificationOptions>().BindConfiguration(NotificationOptions.SectionName)
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<WorkerOptions>().BindConfiguration(WorkerOptions.SectionName)
    .ValidateDataAnnotations().ValidateOnStart();

var databaseOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
var redisOptions = builder.Configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>() ?? new RedisOptions();
var identityOptions = builder.Configuration.GetSection(VeritasIdentityOptions.SectionName).Get<VeritasIdentityOptions>() ?? new VeritasIdentityOptions();
var rateLimitOptions = builder.Configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();
var securityOptions = builder.Configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();

// ---------------------------------------------------------------------------
// Data: PostgreSQL is the system of truth (ADR-002).
// ---------------------------------------------------------------------------
var postgresConnection = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(postgresConnection))
    throw new InvalidOperationException(
        "ConnectionStrings:Postgres is not configured. Set it via environment variable " +
        "ConnectionStrings__Postgres or a secret manager; credentials are never committed.");

builder.Services.AddDbContext<VeritasDbContext>(options =>
    options.UseNpgsql(postgresConnection, npgsql =>
        npgsql.CommandTimeout(databaseOptions.CommandTimeoutSeconds)
              .MaxBatchSize(64)
              .EnableRetryOnFailure(maxRetryCount: 3)));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();

// ---------------------------------------------------------------------------
// Redis: latency infrastructure only. If it is unreachable the app keeps
// working against PostgreSQL (ADR-003) — nothing here is correctness-critical.
// ---------------------------------------------------------------------------
var redisConnection = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    try
    {
        var multiplexer = ConnectionMultiplexer.Connect(new ConfigurationOptions
        {
            EndPoints = { redisConnection },
            AbortOnConnectFail = false,
            ConnectTimeout = redisOptions.ConnectTimeoutMs
        });
        builder.Services.AddSingleton<IConnectionMultiplexer>(multiplexer);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Initial Redis connection failed; caching/idempotency/rate limiting will fail open.");
    }
}

builder.Services.AddScoped<IPolicyCache, RedisPolicyCache>();
builder.Services.AddScoped<IIdempotencyService, RedisIdempotencyService>();

// ---------------------------------------------------------------------------
// Identity (spec section 7)
// ---------------------------------------------------------------------------
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
    {
        options.Password.RequiredLength = identityOptions.PasswordMinLength;
        options.Password.RequireNonAlphanumeric = identityOptions.RequireNonAlphanumeric;
        options.Password.RequireUppercase = identityOptions.RequireUppercase;
        options.Password.RequireLowercase = identityOptions.RequireLowercase;
        options.Password.RequireDigit = identityOptions.RequireDigit;
        options.Lockout.MaxFailedAccessAttempts = identityOptions.MaxFailedAccessAttempts;
        options.Lockout.DefaultLockoutTimeSpan = identityOptions.LockoutDuration;
        options.SignIn.RequireConfirmedEmail = identityOptions.RequireConfirmedEmail;
        options.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<VeritasDbContext>()
    .AddClaimsPrincipalFactory<Veritas.Web.Modules.Identity.Infrastructure.VeritasClaimsPrincipalFactory>()
    .AddDefaultTokenProviders();

// Re-issues the principal (org_id, lifecycle_state) on an interval so a
// mid-session Suspend/Revoke takes effect without waiting for re-login.
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
    options.ValidationInterval = identityOptions.SecurityStampValidationInterval);

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Identity/Account/Login";
    options.AccessDeniedPath = "/Identity/Account/AccessDenied";
    options.Cookie.Name = "veritas.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = identityOptions.SessionLifetime;
    options.SlidingExpiration = true;
});

// ---------------------------------------------------------------------------
// Application services. Every module registers its own Application-layer
// interface; nothing resolves another module's DbContext or entities (spec 4).
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<IPolicyEvaluationEngine, PolicyEvaluationEngine>();
builder.Services.AddSingleton<VeritasMetrics>();

builder.Services.AddScoped<IAuthorizationService, AuthorizationService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IAuditQueryService, AuditQueryService>();
builder.Services.AddScoped<IRiskEvaluationService, RiskEvaluationService>();
builder.Services.AddScoped<IRiskDashboardService, RiskDashboardService>();
builder.Services.AddScoped<IRbacEvaluator, RbacEvaluator>();
builder.Services.AddScoped<ITemporaryGrantReader, TemporaryGrantReader>();
builder.Services.AddScoped<ISeparationOfDutiesEvaluator, SeparationOfDutiesEvaluator>();
builder.Services.AddScoped<IRoleAssignmentService, RoleAssignmentService>();
builder.Services.AddScoped<IRoleService, RoleService>();
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddScoped<IResourceService, ResourceService>();
builder.Services.AddScoped<IPolicyManagementService, PolicyManagementService>();
builder.Services.AddScoped<IPolicySimulatorService, PolicySimulatorService>();
builder.Services.AddScoped<IAccessRequestService, AccessRequestService>();
builder.Services.AddScoped<IApprovalWorkflowService, ApprovalWorkflowService>();
builder.Services.AddScoped<IPrivilegedAccessService, PrivilegedAccessService>();
builder.Services.AddScoped<IIdentityUserService, IdentityUserService>();
builder.Services.AddScoped<IUserLifecycleService, UserLifecycleService>();
builder.Services.AddScoped<IApiKeyService, ApiKeyService>();
builder.Services.AddScoped<IAccessReviewService, AccessReviewService>();
builder.Services.AddScoped<IComplianceService, ComplianceService>();
builder.Services.AddScoped<IControlCenterService, ControlCenterService>();
builder.Services.AddScoped<ISystemStatusService, SystemStatusService>();
builder.Services.AddScoped<IAccessGraphQueryService, AccessGraphQueryService>();

builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<INotificationTransport, LoggingEmailTransport>();
builder.Services.AddScoped<INotificationTransport, InAppNotificationTransport>();
builder.Services.AddScoped<INotificationTransport, WebhookNotificationTransport>();
builder.Services.AddHttpClient(nameof(WebhookNotificationTransport));

builder.Services.AddAuthorization();

// ---------------------------------------------------------------------------
// Background workers (spec section 35). Each creates its own DI scope per tick.
// ---------------------------------------------------------------------------
builder.Services.AddHostedService<TemporaryAccessExpirationWorker>();
builder.Services.AddHostedService<SecurityDetectionWorker>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddHostedService<AccessReviewWorker>();
builder.Services.AddHostedService<RiskEvaluationWorker>();
builder.Services.AddHostedService<AuditProcessingWorker>();
builder.Services.AddHostedService<CleanupWorker>();

// ---------------------------------------------------------------------------
// Rate limiting (spec section 36) — one policy per API surface, 429 on breach.
// ---------------------------------------------------------------------------
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter =
            context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                ? ((int)retryAfter.TotalSeconds).ToString()
                : "1";

        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            type = "https://tools.ietf.org/html/rfc6585#section-4",
            title = "Too Many Requests",
            status = StatusCodes.Status429TooManyRequests,
            detail = "Rate limit exceeded for this endpoint. Retry after the indicated interval."
        }, ct);
    };

    options.AddPolicy("authorization-api", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(1),
                PermitLimit = rateLimitOptions.AuthorizationPermitsPerSecond,
                QueueLimit = rateLimitOptions.AuthorizationQueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));

    options.AddPolicy("login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"login:{httpContext.Connection.RemoteIpAddress}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = rateLimitOptions.LoginPermitsPerMinute,
                QueueLimit = 0
            }));

    options.AddPolicy("admin-api", _ =>
        RateLimitPartition.GetFixedWindowLimiter("admin", _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromSeconds(1),
            PermitLimit = rateLimitOptions.AdminApiPermitsPerSecond,
            QueueLimit = 5
        }));

    options.AddPolicy("access-request-api", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.Identity?.Name ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(1),
                PermitLimit = rateLimitOptions.AccessRequestPermitsPerSecond,
                QueueLimit = 10
            }));

    options.AddPolicy("audit-api", _ =>
        RateLimitPartition.GetFixedWindowLimiter("audit", _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromSeconds(1),
            PermitLimit = rateLimitOptions.AuditPermitsPerSecond,
            QueueLimit = 5
        }));

    options.AddPolicy("apikey-auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Request.Headers.Authorization.FirstOrDefault() ?? "anon",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(1),
                PermitLimit = rateLimitOptions.ApiKeyAuthPermitsPerSecond,
                QueueLimit = 10
            }));
});

// ---------------------------------------------------------------------------
// Observability: OpenTelemetry tracing + metrics (spec section 40)
// ---------------------------------------------------------------------------
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("Veritas.Web"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddSource(VeritasMetrics.ActivitySourceName)
        .AddConsoleExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddMeter(VeritasMetrics.MeterName)
        .AddConsoleExporter());

builder.Services.AddScoped<Veritas.Web.Infrastructure.Seeding.DemoDataSeeder>();

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();
builder.Services.AddEndpointsApiExplorer();

// FluentValidation: validators are scanned from this assembly and invoked
// explicitly in mutating controller actions, keeping the validation path
// visible and unit-testable.
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

builder.Services.Configure<ApiBehaviorOptions>(options =>
    options.SuppressModelStateInvalidFilter = false);

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "VERITAS Authorization API",
        Version = "v1",
        Description = "Zero-Trust authorization decisions, explained and reproducible. " +
                      "Every endpoint evaluates real policy, RBAC, risk and grant state — " +
                      "no endpoint returns fabricated or placeholder results."
    });

    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (System.IO.File.Exists(xmlPath))
        c.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);

    c.TagActionsBy(api => new[] { api.ActionDescriptor.RouteValues["controller"] ?? "default" });

    c.AddSecurityDefinition("cookie", new()
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        In = Microsoft.OpenApi.Models.ParameterLocation.Cookie,
        Name = "veritas.session",
        Description = "ASP.NET Core Identity session cookie issued by /Identity/Account/Login."
    });
    c.AddSecurityRequirement(new()
    {
        [new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "cookie" } }]
            = Array.Empty<string>()
    });
});

// Health: /health/live is a pure liveness probe (no dependencies),
// /health/ready proves PostgreSQL is reachable, /health aggregates.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: new[] { "live" })
    .AddCheck<DatabaseHealthCheck>("postgres", tags: new[] { "ready" });

var app = builder.Build();

// --- Correlation IDs: generated if absent, echoed back, pushed into logs ----
app.Use(async (context, next) =>
{
    const string header = "X-Correlation-ID";
    var correlationId = context.Request.Headers[header].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId))
        correlationId = context.TraceIdentifier;

    context.Items[HomeController.CorrelationIdItemKey] = correlationId;
    context.Response.Headers[header] = correlationId;

    using (LogContext.PushProperty("CorrelationId", correlationId))
    using (LogContext.PushProperty("TenantId", context.User.FindFirst("org_id")?.Value))
    using (LogContext.PushProperty("UserId", context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value))
    {
        await next();
    }
});

// --- Security headers (spec section 38) -------------------------------------
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=(), payment=(), usb=()";
    headers["Cross-Origin-Opener-Policy"] = "same-origin";
    headers["Cross-Origin-Resource-Policy"] = "same-origin";

    // style-src keeps 'unsafe-inline' because the skeuomorphic gauges and risk
    // bars set their extent through CSS custom properties on the element; script
    // sources remain strictly 'self', so no page can execute injected script.
    headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "object-src 'none'";

    if (securityOptions.EnableHsts)
    {
        headers["Strict-Transport-Security"] =
            $"max-age={securityOptions.HstsMaxAgeDays * 24 * 60 * 60}; includeSubDomains";
    }

    await next();
});

// --- Centralized exception handling (spec section 39) -----------------------
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var feature = context.Features.Get<IExceptionHandlerFeature>();
    var correlationId = context.Items[HomeController.CorrelationIdItemKey] as string ?? context.TraceIdentifier;

    Log.Error(feature?.Error, "Unhandled exception for {Path} (correlation {CorrelationId})",
        context.Request.Path, correlationId);

    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new
        {
            type = "https://tools.ietf.org/html/rfc7231#section-6.6.1",
            title = "An unexpected error occurred.",
            status = 500,
            detail = "The request could not be completed. No diagnostic detail is exposed by design.",
            instance = context.Request.Path.Value,
            correlationId
        });
        return;
    }

    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.Redirect($"/Home/Error?correlationId={Uri.EscapeDataString(correlationId)}");
}));

if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseHttpsRedirection();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.CacheControl = "public,max-age=3600";
    }
});

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "VERITAS API v1");
    c.RoutePrefix = "swagger";
});

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

app.MapControllers();
app.MapRazorPages();

app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => true
});
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live")
});
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

// ---------------------------------------------------------------------------
// Schema + seed.
//
// Production schema changes go through EF Core migrations generated by
// scripts/generate-migrations.sh and applied by the deployment pipeline
// (Database:AutoMigrate=true). Development may create the schema directly from
// the current model, which keeps `docker compose up` a single command.
// ---------------------------------------------------------------------------
if (databaseOptions.AutoMigrate)
{
    using var migrationScope = app.Services.CreateScope();
    var migrationDb = migrationScope.ServiceProvider.GetRequiredService<VeritasDbContext>();
    await migrationDb.Database.MigrateAsync();
    Log.Information("Applied pending EF Core migrations.");
}
else if (app.Environment.IsDevelopment())
{
    using var devScope = app.Services.CreateScope();
    var devDb = devScope.ServiceProvider.GetRequiredService<VeritasDbContext>();
    try
    {
        await devDb.Database.EnsureCreatedAsync();
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Development schema initialization failed; the app will still start.");
    }
}

if (app.Environment.IsDevelopment())
{
    using var seedScope = app.Services.CreateScope();
    var seeder = seedScope.ServiceProvider.GetRequiredService<Veritas.Web.Infrastructure.Seeding.DemoDataSeeder>();
    try
    {
        await seeder.SeedAsync();
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Demo data seeding failed (non-fatal). Common cause: PostgreSQL not reachable yet.");
    }
}

app.Run();

public partial class Program { }
