using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using ZyncMaster.Server;
using ZyncMaster.Server.Data;
using ZyncMaster.Server.Infrastructure.Email;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Server"));
builder.Services.Configure<ZyncMaster.Server.Configuration.MailjetOptions>(builder.Configuration.GetSection("Mailjet"));

// ForwardedHeaders for running behind the Azure App Service reverse proxy. App Service terminates
// TLS at its front-end and forwards over plain http with X-Forwarded-For / X-Forwarded-Proto set;
// without this the app sees the proxy as the client (RemoteIpAddress = the front-end, scheme =
// http), which would (a) collapse the per-IP magic-link rate limiter onto a single proxy address
// and (b) make UseHttpsRedirection loop. We honour XFF + XFF-Proto and CLEAR KnownIPNetworks /
// KnownProxies. Clearing the known-proxy allow-list normally means "trust the header from any
// hop" (spoofable on an open network), but on Azure App Service it is safe: the App Service
// front-end is the ONLY ingress and it OVERWRITES (not appends) the X-Forwarded-* headers with
// the real client values before they reach the app, so a client-supplied XFF cannot survive. The
// app is never directly reachable on the public internet. This is the configuration Microsoft
// documents for App Service / containers behind an unknown-IP proxy.
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders =
        Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
        Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

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

// Fail-fast on the OAuth / magic-link critical config — same discipline as the connection-string
// guard above. Gated on !IsDevelopment() so the WebApplicationFactory test host and local dev
// (both Development, with empty config) keep starting; in production a blank MicrosoftClientId /
// redirect URI / PublicBaseUrl aborts host build with a clear, aggregated message rather than
// serving a broken sign-in flow. Mailjet is NOT validated here (optional, falls back to logging).
ZyncMaster.Server.Configuration.StartupConfigValidator.ValidateOAuthConfig(
    builder.Configuration.GetSection("Server").Get<ServerOptions>() ?? new ServerOptions(),
    builder.Environment.IsDevelopment());

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
// Per-request current-user override seam (used by the cron runner to execute a pair under its
// owner's identity). Singleton over IHttpContextAccessor, like the accessor it pairs with.
builder.Services.AddSingleton<IHttpCurrentUserOverride, HttpCurrentUserOverride>();
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

// Outbound email seam for the magic-link flow. Conditional registration: when BOTH Mailjet
// credentials are configured (Mailjet__ApiKey + Mailjet__ApiSecret), the real Mailjet REST v3.1
// transport is wired up via a typed HttpClient; otherwise the dev/test default (LoggingEmailSender,
// logs and sends nothing) stays in place so a no-config run never breaks the magic-link flow.
// Mailjet is OPTIONAL — it is intentionally NOT part of the startup fail-fast.
var mailjetOptions = builder.Configuration.GetSection("Mailjet").Get<ZyncMaster.Server.Configuration.MailjetOptions>()
    ?? new ZyncMaster.Server.Configuration.MailjetOptions();
if (!string.IsNullOrWhiteSpace(mailjetOptions.ApiKey)
    && !string.IsNullOrWhiteSpace(mailjetOptions.ApiSecret))
{
    builder.Services.AddHttpClient<IEmailSender, MailjetEmailSender>();
}
else
{
    builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
}

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
builder.Services.AddSingleton<DeviceNameGenerator>();
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

// Track C — plan/entitlements seam. Today's impl returns everything unlocked (∩ the user's
// toggles). SWAP: replace DefaultEntitlementsService with PlanBasedEntitlementsService here (one
// line) when plan gating (Free/PRO) goes live; nothing downstream changes.
builder.Services.AddSingleton<IEntitlementsService, DefaultEntitlementsService>();

// §D-1 — the external cron-trigger runner. Singleton over the DbContext factory (cross-user
// queries) + the run-lock + the module registry; it executes every due, uncovered pair when the
// external scheduler hits /api/sync/run-due.
builder.Services.AddSingleton<CronSyncRunner>();

// §A/§D — ephemeral-table hygiene. Background sweep that set-deletes expired identity tokens,
// expired/consumed magic-links and expired run-locks (see EphemeralPurgeService for the token
// safety rule). Registered ONLY outside Development so the WebApplicationFactory test host never
// starts the timer loop (the purge logic stays unit-tested via PurgeOnceAsync); production runs it.
if (!builder.Environment.IsDevelopment())
    builder.Services.AddHostedService<EphemeralPurgeService>();

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
        // Azure SQL (especially on the lower tiers / when waking) frequently resets the FIRST
        // login TCP handshake on a cold start: "Connection reset by peer" / SqlException 35 during
        // login. A single Migrate() attempt that hits that reset would kill the process and the
        // container never binds its port -> Azure reports "did not start within 230s". So retry the
        // migration with backoff, treating the first transient failures as the DB still coming up;
        // only refuse to start (fail-fast against a half-migrated DB) once retries are exhausted.
        const int maxAttempts = 8;
        var delay = TimeSpan.FromSeconds(3);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                logger.LogInformation("Applying pending database migrations (attempt {Attempt}/{Max}).", attempt, maxAttempts);
                db.Database.Migrate();
                logger.LogInformation("Database migrations applied.");
                break;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                // Transient on a cold DB — log a warning and back off (capped) rather than crash.
                logger.LogWarning(ex,
                    "Database migration attempt {Attempt}/{Max} failed; retrying in {Delay}s.",
                    attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.8, 20));
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Database migration failed after {Max} attempts; refusing to start.", maxAttempts);
                throw;
            }
        }
    }
}

// Honour the Azure App Service reverse-proxy forwarded headers FIRST, before any middleware
// that reads the client IP or scheme — the per-IP magic-link rate limiter and UseHttpsRedirection
// both depend on RemoteIpAddress / Request.Scheme reflecting the real client, not the proxy. See
// the ForwardedHeadersOptions registration above for why clearing the known-proxy list is safe on
// App Service (the front-end overwrites X-Forwarded-* and is the only ingress).
app.UseForwardedHeaders();

// Security headers (L3). Applied to every response, including static panel/UI assets.
//   X-Frame-Options: DENY        — the panel is never meant to be framed (clickjacking).
//   X-Content-Type-Options: nosniff — stop MIME sniffing of served assets.
//   Referrer-Policy: no-referrer — don't leak the panel URL to outbound navigations.
//
// Content-Security-Policy (§C). Tuned against the THREE served surfaces so none break:
//   * the landing (web/index.html) — external <script src=js/…> + inline style= attributes;
//   * the dashboard (ui/index.html) — external <script type=module src=js/…> + inline style=;
//   * the server-rendered /pair approval page — an inline <script> wiring the Approve button
//     (and the /connect, magic-link, calendar-connect HTML pages, all 'self'-only otherwise).
// Directives, and why each is exactly this:
//   default-src 'self'            — same-origin only baseline; the project ships NO CDNs, so
//                                    everything (fetch /api/*, assets) is first-party.
//   img-src 'self' data:          — UI/landing use small inline data: URIs (icons/SVG); no
//                                    remote images.
//   style-src 'self' 'unsafe-inline' — both index.html files use inline style="…" attributes
//                                    for layout (e.g. --nav-count, centring); CSP cannot allow
//                                    a style attribute without 'unsafe-inline' (per-attr hashes
//                                    are not supported), so it is required to not break the UI.
//   script-src 'self' 'unsafe-inline' — the /pair page emits an inline <script> to POST the
//                                    approval; it is server-rendered and human-reached, not an
//                                    XSS sink. Allowing 'unsafe-inline' here keeps that page
//                                    working without a refactor; first-party js/*.js still load
//                                    via 'self'. (Hardening to a nonce/hash is a later follow-up
//                                    once /pair's inline script is externalised.)
//   object-src 'none'             — no plugins/embeds.
//   base-uri 'self'               — block <base> hijacking of relative asset URLs.
//   frame-ancestors 'none'        — modern equivalent of X-Frame-Options: DENY.
const string contentSecurityPolicy =
    "default-src 'self'; " +
    "img-src 'self' data:; " +
    "style-src 'self' 'unsafe-inline'; " +
    "script-src 'self' 'unsafe-inline'; " +
    "object-src 'none'; " +
    "base-uri 'self'; " +
    "frame-ancestors 'none'";
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Frame-Options"] = "DENY";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Content-Security-Policy"] = contentSecurityPolicy;
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
app.MapSyncRunDueEndpoints();
app.MapPanelEndpoints();
app.MapPairEndpoints();
app.MapPairApprovalEndpoints();
app.MapEntitlementEndpoints();

app.Run();

public partial class Program { }
