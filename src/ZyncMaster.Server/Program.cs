using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using ZyncMaster.Server;
using ZyncMaster.Server.Data;
using ZyncMaster.Server.Infrastructure.Email;
using ZyncMaster.Server.Modules.Calendar;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Server"));

// EF Core persistence. A DbContextFactory (singleton) backs the stores so they can be
// shared by the singleton composition root; a scoped DbContext is also registered for
// the Data Protection key ring.
//
// Connection string resolution: production MUST supply "ConnectionStrings:ZyncMasterDb"
// (in Azure App Service this comes from the connection-strings blade — type "SQLAzure" —
// surfaced to the app as the env var SQLAZURECONNSTR_ZyncMasterDb, or equivalently an app
// setting "ConnectionStrings__ZyncMasterDb"). The LocalDB-style fallback below is ONLY used
// in the Development environment; in any other environment a missing connection string is a
// fail-fast configuration error rather than a silent fall-through to a non-existent LocalDB.
var connectionString = builder.Configuration.GetConnectionString("ZyncMasterDb");
if (string.IsNullOrWhiteSpace(connectionString))
{
    if (builder.Environment.IsDevelopment())
    {
        connectionString =
            "Server=(localdb)\\MSSQLLocalDB;Database=ZyncMaster;Trusted_Connection=True;MultipleActiveResultSets=true";
    }
    else
    {
        throw new InvalidOperationException(
            "Missing required connection string 'ConnectionStrings:ZyncMasterDb'. " +
            "In production set it via the Azure App Service connection-strings blade (type SQLAzure) " +
            "or the app setting 'ConnectionStrings__ZyncMasterDb'.");
    }
}

builder.Services.AddDbContextFactory<ZyncMasterDbContext>(o => o.UseSqlServer(connectionString));
builder.Services.AddDbContext<ZyncMasterDbContext>(
    o => o.UseSqlServer(connectionString),
    contextLifetime: ServiceLifetime.Scoped,
    optionsLifetime: ServiceLifetime.Singleton);

builder.Services.AddDataProtection()
    .PersistKeysToDbContext<ZyncMasterDbContext>()
    .SetApplicationName("ZyncMaster");

builder.Services.AddHttpClient<IMicrosoftTokenService, MicrosoftTokenService>();
builder.Services.AddHttpClient("graph");

// The accessor is a SINGLETON over IHttpContextAccessor and reads HttpContext per call,
// so injecting it into the singleton EF stores does not capture a single request's user.
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ICurrentUserAccessor, HttpContextCurrentUserAccessor>();
builder.Services.AddSingleton<IUserStore, EfUserStore>();
builder.Services.AddSingleton<IDeviceStore, EfDeviceStore>();
builder.Services.AddSingleton<ISyncStateStore, EfSyncStateStore>();
builder.Services.AddSingleton<ISecretProvider, ConfigurationSecretProvider>();
builder.Services.AddSingleton<IConnectedAccountStore, EfConnectedAccountStore>();
builder.Services.AddSingleton<ICalendarAccountStore, EfCalendarAccountStore>();

// §C-3 — the one place that bridges the legacy per-UPN store and the new accountId pool. It is
// resolved everywhere the pair/token code needs to turn an Endpoint.AccountRef (which may be a
// legacy UPN or a real pool accountId) into a canonical accountId and fetch/rotate its token.
builder.Services.AddSingleton<ILegacyConnectedAccountAdapter, LegacyConnectedAccountAdapter>();

// System clock seam shared by the identity primitives (overridden in tests for determinism).
builder.Services.AddSingleton(TimeProvider.System);

// Identity session tokens (internal Server<->App bearer) + one-time loopback handles.
// Both are singletons: the token service is stateless over the singleton DbContextFactory,
// and the handle store holds its in-memory map for the lifetime of the (single) instance.
builder.Services.AddSingleton<IIdentityTokenService, DataProtectionIdentityTokenService>();
builder.Services.AddSingleton<IIdentityHandleStore, InMemoryIdentityHandleStore>();

// Outbound email seam for the magic-link flow. The dev/test default logs and sends nothing;
// the real SendGrid transport (plan deferred §4) is a one-line swap here in production.
builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();

// Per-IP rate limiter for the magic-link POST (plan A-6). A fixed window keyed on the remote IP
// rejects raw endpoint abuse with 429. This is anti-abuse only — it does not branch on whether
// the email exists, so it leaks no user-existence information. The per-EMAIL limit (silent, in
// the endpoint) is what stays constant-time for anti-enumeration.
builder.Services.AddRateLimiter(rl =>
{
    rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rl.AddPolicy(IdentityMagicLinkEndpoints.PerIpRateLimitPolicy, context =>
    {
        var opts = context.RequestServices.GetRequiredService<IOptions<ServerOptions>>().Value;
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = opts.MagicLinkMaxPerIp,
            Window = TimeSpan.FromMinutes(opts.MagicLinkRateLimitWindowMinutes),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    });
});
builder.Services.AddScoped<DeviceService>();
builder.Services.AddScoped<SyncService>();

// §C-5 — the legacy /api/sync target factory now resolves the token by accountId through the
// adapter. The incoming string is the legacy accountRef (UPN / "default"); the adapter derives
// the canonical accountId and reads the token from whichever store backs it, so single-account
// devices keep syncing unchanged while the resolution path is unified on accountId.
builder.Services.AddSingleton<Func<string, ZyncMaster.Graph.ICalendarTarget>>(sp => accountRef =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("graph");
    var tokens = sp.GetRequiredService<IMicrosoftTokenService>();
    var adapter = sp.GetRequiredService<ILegacyConnectedAccountAdapter>();
    var opts = sp.GetRequiredService<IOptions<ServerOptions>>().Value;
    var provider = new AccountAwareGraphTokenProvider(tokens, adapter, accountRef);
    return new ZyncMaster.Graph.GraphCalendarTarget(http, provider, Guid.Parse(opts.ExtendedPropertyGuid));
});

// §C-5 — per-account Microsoft Graph provider (reader + writer) for pair sync. The accountRef
// carried on the pair endpoint may be a real pool accountId or a legacy UPN; the adapter-backed
// token provider resolves either to the right refresh token, bridging legacy pairs to the pool.
builder.Services.AddSingleton<Func<string?, MicrosoftGraphProvider>>(sp => accountRef =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("graph");
    var readHttp = sp.GetRequiredService<IHttpClientFactory>().CreateClient("graph");
    var tokens = sp.GetRequiredService<IMicrosoftTokenService>();
    var adapter = sp.GetRequiredService<ILegacyConnectedAccountAdapter>();
    var opts = sp.GetRequiredService<IOptions<ServerOptions>>().Value;
    var tokenProvider = new AccountAwareGraphTokenProvider(tokens, adapter, accountRef);
    var target = new ZyncMaster.Graph.GraphCalendarTarget(http, tokenProvider, Guid.Parse(opts.ExtendedPropertyGuid));
    return new MicrosoftGraphProvider(readHttp, tokenProvider, target);
});
builder.Services.AddSingleton<ProviderRegistry>();

// Modular sync engine seam (Phase 4): a pair's execution lives behind ISyncModule so adding
// Files/Clipboard later is "new module + tile", not a rewrite of the run engine. Today only
// the calendar module is registered; it wraps the read+mirror that /run delegates to. The
// per-pair run-lock stays in the endpoint and wraps the module call.
builder.Services.AddSingleton<ICalendarSyncModule, CalendarSyncModule>();
builder.Services.AddSingleton(sp =>
{
    var registry = new SyncModuleRegistry();
    registry.Register(sp.GetRequiredService<ICalendarSyncModule>());
    return registry;
});

builder.Services.AddSingleton<ISyncPairStore, EfSyncPairStore>();
builder.Services.AddSingleton<ISyncRunLock, EfSyncRunLock>();

// ApiKey stays the default scheme so device endpoints (/api/*) keep working as before;
// the Cookie scheme is added alongside it for the human panel. Cookies are HttpOnly,
// SameSite=Lax, sliding-expiration, and Secure only over https (so the http test host
// still round-trips the cookie).
builder.Services.AddAuthentication(AuthSchemes.ApiKey)
    .AddApiKeyAuth()
    .AddIdentityBearerAuth()
    .AddCookie(AuthSchemes.Cookie, options =>
    {
        options.Cookie.Name = "sm_session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        // The panel is an API surface, not an MVC app: return status codes instead of
        // redirecting unauthenticated/forbidden callers to a login/access-denied page.
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Apply pending EF Core migrations on startup against the configured SQL Server database.
// This is guarded by IsSqlServer() so it runs for the real app but is SKIPPED under the
// WebApplicationFactory test harness, which swaps the context to the SQLite provider and
// builds its schema with EnsureCreated() — calling Migrate() on a SQLite/EnsureCreated DB
// would throw on the relational-mismatch. The provider check is robust regardless of how
// the test host is configured. Fail-fast: if migration throws we log and rethrow rather
// than serve traffic against a half-migrated database.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
    if (db.Database.IsSqlServer())
    {
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Startup.Migrate");
        try
        {
            logger.LogInformation("Applying pending database migrations.");
            db.Database.Migrate();
            logger.LogInformation("Database migrations applied.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Database migration failed on startup; refusing to start.");
            throw;
        }
    }
}

// Security headers (L3). Applied to every response, including static panel/UI assets.
//   X-Frame-Options: DENY        — the panel is never meant to be framed (clickjacking).
//   X-Content-Type-Options: nosniff — stop MIME sniffing of served assets.
//   Referrer-Policy: no-referrer — don't leak the panel URL to outbound navigations.
// NOTE: no Content-Security-Policy header is set yet. A CSP would break the /pair page's
// inline <script> and the UI's inline style attributes; it needs to be tuned against the UI
// (hashes/nonces) before it can be enabled. TODO: add a tuned CSP as a follow-up.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Frame-Options"] = "DENY";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    await next();
});

// Transport hardening only outside Development. The WebApplicationFactory test host runs as
// Development over plain http, so gating on !IsDevelopment() keeps the test host unaffected
// (no HSTS, no forced https redirect) while production gets both.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

// Rate limiter middleware. Endpoints opt in via RequireRateLimiting; only the magic-link POST
// does today. Placed after auth so the limiter sees the resolved connection.
app.UseRateLimiter();

// Static files. Two surfaces are served:
//
//   /      -> the marketing LANDING / launcher (repo-root web/). It owns the site root.
//   /app   -> the canonical Liquid Glass dashboard UI (repo-root ui/). The same ui/ folder
//             is the single source of truth shared with the desktop App, which bundles its
//             OWN copy and loads it through a virtual host (it does NOT go through this
//             server), so serving the dashboard under /app here does not affect the App.
//
// In a normal run the content root is src/ZyncMaster.Server, so ../../web and ../../ui
// resolve to the repo folders; the same holds under the WebApplicationFactory test host
// (project dir content root). In a published layout web/ is bundled into wwwroot/ and ui/
// into wwwroot/app/, so we fall back to those when the repo folders are absent.
var webRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "web"));
var uiRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "ui"));

var publishedAppDir = Path.Combine(app.Environment.WebRootPath ?? "", "app");
var landingFileProvider = Directory.Exists(webRoot)
    ? new PhysicalFileProvider(webRoot)
    : app.Environment.WebRootFileProvider;
var dashboardFileProvider = Directory.Exists(uiRoot)
    ? new PhysicalFileProvider(uiRoot)
    : (Directory.Exists(publishedAppDir)
        ? new PhysicalFileProvider(publishedAppDir)
        : app.Environment.WebRootFileProvider);

// Dashboard at /app. Redirect ONLY the bare "/app" (no trailing slash) to "/app/" so the
// dashboard's relative asset links (css/…, js/…) resolve under /app/ rather than the site
// root. This is a path-exact middleware check rather than a routed endpoint so it does not
// also capture "/app/" (which routing would normalise to the same route) and loop.
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/app")
    {
        context.Response.Redirect("/app/" + context.Request.QueryString);
        return;
    }
    await next();
});

var dashboardDefaults = new DefaultFilesOptions
{
    FileProvider = dashboardFileProvider,
    RequestPath = "/app",
};
dashboardDefaults.DefaultFileNames.Clear();
dashboardDefaults.DefaultFileNames.Add("index.html");
app.UseDefaultFiles(dashboardDefaults);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = dashboardFileProvider,
    RequestPath = "/app",
});

// Landing at the site root.
var landingDefaults = new DefaultFilesOptions { FileProvider = landingFileProvider };
landingDefaults.DefaultFileNames.Clear();
landingDefaults.DefaultFileNames.Add("index.html");
app.UseDefaultFiles(landingDefaults);
app.UseStaticFiles(new StaticFileOptions { FileProvider = landingFileProvider });

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapDeviceEndpoints();
app.MapConnectEndpoints();
app.MapIdentityConnectEndpoints();
app.MapIdentityMagicLinkEndpoints();
app.MapCalendarConnectEndpoints();
app.MapSyncEndpoints();
app.MapPanelEndpoints();
app.MapPairEndpoints();
app.MapPairApprovalEndpoints();

app.Run();

public partial class Program { }
