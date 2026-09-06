using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using StackExchange.Redis;
using Veritas.Web.BackgroundWorkers;
using Veritas.Web.Infrastructure.Caching;
using Veritas.Web.Infrastructure.Idempotency;
using Veritas.Web.Modules.AccessReview.Application;
using Veritas.Web.Modules.ApplicationRegistry.Application;
using Veritas.Web.Modules.Audit.Application;
using Veritas.Web.Modules.Authorization.Application;
using Veritas.Web.Modules.Approval.Application;
using Veritas.Web.Modules.Identity.Domain;
using Veritas.Web.Modules.Notification.Application;
using Veritas.Web.Modules.PolicyManagement.Application;
using Veritas.Web.Modules.PrivilegedAccess.Application;
using Veritas.Web.Modules.RiskManagement.Application;
using Veritas.Web.Modules.RoleManagement.Application;
using FluentValidation;
using Veritas.Web.Observability;
using Veritas.Web.Shared.Domain;
using Veritas.Web.Shared.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Veritas")
    .WriteTo.Console());

// --- Data ---
builder.Services.AddDbContext<VeritasDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")
        ?? "Host=localhost;Database=veritas;Username=veritas;Password=veritas"));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();

// --- Redis (optional infra: app must still run if this is unreachable, see ADR-003) ---
var redisConnectionString = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    try
    {
        var multiplexer = ConnectionMultiplexer.Connect(new ConfigurationOptions
        {
            EndPoints = { redisConnectionString },
            AbortOnConnectFail = false,
            ConnectTimeout = 2000
        });
        builder.Services.AddSingleton<IConnectionMultiplexer>(multiplexer);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Could not establish initial Redis connection at startup; caching/rate limiting/idempotency will fail open until Redis is reachable.");
    }
}

builder.Services.AddScoped<IPolicyCache, RedisPolicyCache>();
builder.Services.AddScoped<IIdempotencyService, RedisIdempotencyService>();

// --- Identity ---
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
    {
        options.Password.RequiredLength = 12;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequireUppercase = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.SignIn.RequireConfirmedEmail = true;
    })
    .AddEntityFrameworkStores<VeritasDbContext>()
    .AddClaimsPrincipalFactory<Veritas.Web.Modules.Identity.Infrastructure.VeritasClaimsPrincipalFactory>()
    .AddDefaultTokenProviders();

// Re-issues the claims principal (including org_id, lifecycle_state) every 5
// minutes against the live ApplicationUser row, rather than trusting an
// 8-hour-old cookie. This is what makes a mid-session Suspend/Revoke or an
// org change actually take effect promptly instead of only on next login.
builder.Services.Configure<Microsoft.AspNetCore.Identity.SecurityStampValidatorOptions>(options =>
{
    options.ValidationInterval = TimeSpan.FromMinutes(5);
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Identity/Account/Login";
    options.AccessDeniedPath = "/Identity/Account/AccessDenied";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
});

// --- Authorization / policy engine / risk / approvals / privileged access ---
builder.Services.AddSingleton<IPolicyEvaluationEngine, PolicyEvaluationEngine>();
builder.Services.AddScoped<IAuthorizationService, AuthorizationService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IRiskEvaluationService, RiskEvaluationService>();
builder.Services.AddScoped<IApprovalWorkflowService, ApprovalWorkflowService>();
builder.Services.AddScoped<IPrivilegedAccessService, PrivilegedAccessService>();
builder.Services.AddScoped<Veritas.Web.Modules.RoleManagement.Application.ISeparationOfDutiesEvaluator, Veritas.Web.Modules.RoleManagement.Application.SeparationOfDutiesEvaluator>();
builder.Services.AddScoped<IRoleAssignmentService, RoleAssignmentService>();
builder.Services.AddScoped<IPolicySimulatorService, PolicySimulatorService>();
builder.Services.AddScoped<Veritas.Web.Modules.Authorization.Application.IAccessGraphQueryService, Veritas.Web.Modules.Authorization.Application.AccessGraphQueryService>();
builder.Services.AddScoped<IApiKeyService, ApiKeyService>();
builder.Services.AddScoped<IAccessReviewService, AccessReviewService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<Veritas.Web.Modules.Notification.Application.INotificationTransport, Veritas.Web.Modules.Notification.Application.LoggingEmailTransport>();
builder.Services.AddScoped<Veritas.Web.Modules.Notification.Application.INotificationTransport, Veritas.Web.Modules.Notification.Application.InAppNotificationTransport>();
builder.Services.AddScoped<Veritas.Web.Modules.Notification.Application.INotificationTransport, Veritas.Web.Modules.Notification.Application.WebhookNotificationTransport>();
builder.Services.AddHttpClient(nameof(Veritas.Web.Modules.Notification.Application.WebhookNotificationTransport));

builder.Services.AddAuthorization();

// --- Background workers (fresh DI scope per tick, cancellation-aware, idempotent) ---
builder.Services.AddHostedService<TemporaryAccessExpirationWorker>();
builder.Services.AddHostedService<SecurityDetectionWorker>();
builder.Services.AddHostedService<NotificationWorker>();

// --- Rate limiting (spec section 36): distinct policies per API surface ---
builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return ValueTask.CompletedTask;
    };

    options.AddFixedWindowLimiter("authorization-api", opt =>
    {
        opt.Window = TimeSpan.FromSeconds(1);
        opt.PermitLimit = 50;
        opt.QueueLimit = 20;
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });

    options.AddFixedWindowLimiter("login", opt =>
    {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 10;
        opt.QueueLimit = 0;
    });

    options.AddFixedWindowLimiter("admin-api", opt =>
    {
        opt.Window = TimeSpan.FromSeconds(1);
        opt.PermitLimit = 20;
        opt.QueueLimit = 5;
    });

    options.AddFixedWindowLimiter("access-request-api", opt =>
    {
        opt.Window = TimeSpan.FromSeconds(1);
        opt.PermitLimit = 20;
        opt.QueueLimit = 10;
    });
});

// --- Observability: OpenTelemetry tracing + metrics (spec section 40) ---
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

builder.Services.AddSingleton<VeritasMetrics>();
builder.Services.AddScoped<Veritas.Web.Infrastructure.Seeding.DemoDataSeeder>();

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();
builder.Services.AddEndpointsApiExplorer();

// --- FluentValidation: validators registered by scanning this assembly;
// invoked explicitly inside each mutating controller action (see
// AuthorizationApiController, PolicySimulatorController, etc.) rather than
// via an auto-validation MVC filter package, to keep the validation path
// explicit and easy to unit test.
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "VERITAS Authorization API",
        Version = "v1",
        Description = "Zero-Trust authorization decisions, explained and reproducible. " +
                      "Every request is evaluated against real policy/risk/RBAC data — " +
                      "no endpoint here returns fabricated or placeholder results."
    });

    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (System.IO.File.Exists(xmlPath))
        c.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);

    c.TagActionsBy(api => new[] { api.ActionDescriptor.RouteValues["controller"] ?? "default" });
    c.DocInclusionPredicate((docName, apiDesc) => true);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'";
    await next();
});

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "VERITAS API v1"));

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

app.MapControllers();
app.MapRazorPages();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (VeritasDbContext db) =>
{
    var canConnect = await db.Database.CanConnectAsync();
    return canConnect ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
});

// Seeds one realistic demo organization (spec section 60) — idempotent, and
// only runs automatically in Development so it never touches a real
// production database on startup. In other environments, call
// DemoDataSeeder.SeedAsync explicitly (e.g. from a one-off admin endpoint or
// a deployment job) if you want the same fixtures.
if (app.Environment.IsDevelopment())
{
    // The repository intentionally does not check generated EF migrations into source.
    // For the self-contained Development/Compose profile, create the schema directly
    // from the current EF model before seeding. Production deployments should use
    // generated migrations instead.
    using var databaseScope = app.Services.CreateScope();
    var developmentDb = databaseScope.ServiceProvider.GetRequiredService<VeritasDbContext>();
    try
    {
        await developmentDb.Database.EnsureCreatedAsync();
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Development database schema initialization failed; the application will still start.");
    }

    using var seedScope = app.Services.CreateScope();
    var seeder = seedScope.ServiceProvider.GetRequiredService<Veritas.Web.Infrastructure.Seeding.DemoDataSeeder>();
    try
    {
        await seeder.SeedAsync();
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Demo data seeding failed — this is non-fatal, the app will still start. " +
                         "Common cause: PostgreSQL is not reachable yet; verify the database container and connection string.");
    }
}

app.Run();

public partial class Program { }
