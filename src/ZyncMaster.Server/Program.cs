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

// Console logging with scopes on a single line, so the per-request correlation id pushed by the
// request-id middleware (below) shows on every log line and a single request is traceable across
// the journal — addressing the "logs without correlation" gap without taking on a metrics/tracing stack.
builder.Logging.AddSimpleConsole(o => { o.IncludeScopes = true; o.SingleLine = true; });

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
        Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto |
        Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// EF Core persistence. A DbContextFactory (singleton) backs the stores so they can be
// shared by the singleton composition root; a scoped DbContext is also registered for
// the Data Protection key ring.
//
// Connection string resolution: production MUST supply "ConnectionStrings:ZyncMasterDb"
// (on the VPS this is a plain env var ConnectionStrings__ZyncMasterDb read from the systemd
// EnvironmentFile /etc/default/syncmaster, in Npgsql format). The localhost-PostgreSQL fallback
// below is ONLY used in the Development environment; in any other environment a missing
// connection string is a fail-fast configuration error rather than a silent fall-through.
var connectionString = builder.Configuration.GetConnectionString("ZyncMasterDb");
if (string.IsNullOrWhiteSpace(connectionString))
{
    if (builder.Environment.IsDevelopment())
    {
        // Local dev default: a PostgreSQL on localhost. Override via user-secrets / env var
        // ConnectionStrings__ZyncMasterDb. Tests never reach this path (ServerTestFactory swaps SQLite).
        connectionString =
            "Host=localhost;Port=5432;Database=syncmaster_dev;Username=syncmaster_app;Password=devpassword";
    }
    else
    {
        throw new InvalidOperationException(
            "Missing required connection string 'ConnectionStrings:ZyncMasterDb'. " +
            "In production set it via the systemd EnvironmentFile (/etc/default/syncmaster) as " +
            "'ConnectionStrings__ZyncMasterDb' (Npgsql format: Host=...;Port=5432;Database=bd_syncmaster;Username=syncmaster_app;Password=...).");
    }
}

// Fail-fast on the OAuth / magic-link critical config — same discipline as the connection-string
// guard above. Gated on !IsDevelopment() so the WebApplicationFactory test host and local dev
// (both Development, with empty config) keep starting; in production a blank MicrosoftClientId /
// ClientSecret / redirect URI / PublicBaseUrl aborts host build with a clear, aggregated message
// rather than serving a broken sign-in flow. The secret is read from its own config key
// (Microsoft:ClientSecret) because it lives in user-secrets/env vars, NOT the 'Server' section — a
// blank secret would otherwise surface only as an opaque AADSTS error on the first token exchange.
// Mailjet is NOT validated here (optional, falls back to logging).
ZyncMaster.Server.Configuration.StartupConfigValidator.ValidateOAuthConfig(
    builder.Configuration.GetSection("Server").Get<ServerOptions>() ?? new ServerOptions(),
    builder.Environment.IsDevelopment(),
    builder.Configuration["Microsoft:ClientSecret"]);

builder.Services.AddDbContextFactory<ZyncMasterDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddDbContext<ZyncMasterDbContext>(
    o => o.UseNpgsql(connectionString),
    contextLifetime: ServiceLifetime.Scoped,
    optionsLifetime: ServiceLifetime.Singleton);

builder.Services.AddDataProtection()
    .PersistKeysToDbContext<ZyncMasterDbContext>()
    .SetApplicationName("ZyncMaster");

builder.Services.AddHttpClient<IMicrosoftTokenService, MicrosoftTokenService>();
builder.Services.AddHttpClient("graph");

// Best-effort Graph /me lookup used to capture a connected account's real mailbox + display name
// (the calendar token-exchange omits `openid`, so it returns no id_token / email). Typed client
// over the same "graph" pool so no new HTTP dependency is added.
builder.Services.AddHttpClient<IGraphUserInfoService, GraphUserInfoService>();

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

// Best-effort one-time email/displayName backfill for pool accounts connected before /me capture
// existed. Runs on the account listing endpoints; only touches accounts whose email is still blank.
builder.Services.AddSingleton<CalendarAccountEmailBackfill>();

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

    // FIX A — per-IP fixed window for the pairing endpoints (approve + pair/start + pair/complete).
    // Mirrors the magic-link limiter: keyed on the (forwarded-header-resolved) remote IP, rejects
    // excess with 429. This is the brute-force defense for the short pairing code — even with the
    // raised entropy an unthrottled attacker could grind codes against /api/devices/approve, so the
    // window bounds attempts. Anti-abuse only (never branches on whether a code exists), so it leaks
    // nothing about valid codes/users.
    rl.AddPolicy(DeviceEndpoints.PairingRateLimitPolicy, context =>
    {
        var opts = context.RequestServices.GetRequiredService<IOptions<ServerOptions>>().Value;
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter("pairing:" + ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = opts.PairingMaxPerIp,
            Window = TimeSpan.FromMinutes(opts.PairingRateLimitWindowMinutes),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    });

    // FIX 3 — per-IP fixed window for the unauthenticated identity token surfaces
    // (/identity/handle/redeem, /identity/refresh, /identity/magic-link/callback). Each accepts a
    // bearer-style secret directly, so without a limiter they are grindable; this bounds attempts
    // per client address and returns 429 on excess. Anti-abuse only, leaks no user existence.
    rl.AddPolicy(IdentityConnectEndpoints.TokenRateLimitPolicy, context =>
    {
        var opts = context.RequestServices.GetRequiredService<IOptions<ServerOptions>>().Value;
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter("identity-token:" + ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = opts.IdentityTokenMaxPerIp,
            Window = TimeSpan.FromMinutes(opts.IdentityTokenRateLimitWindowMinutes),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    });

    // FIX 4 — per-IP fixed window for the destructive cron trigger (/api/sync/run-due). The endpoint
    // is already secret-gated; this is defense-in-depth so a leaked/guessed secret cannot be paired
    // with unthrottled hammering of cross-user syncs. 429 on excess.
    rl.AddPolicy(SyncRunDueEndpoints.RateLimitPolicy, context =>
    {
        var opts = context.RequestServices.GetRequiredService<IOptions<ServerOptions>>().Value;
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter("cron-run-due:" + ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = opts.CronTriggerMaxPerIp,
            Window = TimeSpan.FromMinutes(opts.CronTriggerRateLimitWindowMinutes),
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

// Calendar v2 — per-account replica client + responder. Keyed by the pool accountId; the
// adapter-backed token provider resolves it to the right refresh token (same seam as the
// pair factories above). The replica GUID is its own constant: ZmReplicaOf events and
// CalImportSourceId events are disjoint by construction (engine separation, spec §7).
builder.Services.AddSingleton<Func<string, ZyncMaster.Graph.IReplicaGraphClient>>(sp => accountId =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("graph");
    var tokens = sp.GetRequiredService<IMicrosoftTokenService>();
    var adapter = sp.GetRequiredService<ILegacyConnectedAccountAdapter>();
    var opts = sp.GetRequiredService<IOptions<ServerOptions>>().Value;
    var provider = new AccountAwareGraphTokenProvider(tokens, adapter, accountId);
    return new ZyncMaster.Graph.GraphReplicaClient(
        http, provider, Guid.Parse(opts.ReplicaPropertyGuid), Guid.Parse(opts.ExtendedPropertyGuid));
});
builder.Services.AddSingleton<Func<string, ZyncMaster.Graph.IEventResponder>>(sp => accountId =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("graph");
    var tokens = sp.GetRequiredService<IMicrosoftTokenService>();
    var adapter = sp.GetRequiredService<ILegacyConnectedAccountAdapter>();
    var provider = new AccountAwareGraphTokenProvider(tokens, adapter, accountId);
    return new ZyncMaster.Graph.GraphEventResponder(http, provider);
});
builder.Services.AddSingleton<IReplicaLinkStore, EfReplicaLinkStore>();
builder.Services.AddSingleton<IPrefixRuleStore, EfPrefixRuleStore>();
builder.Services.AddSingleton<ReplicaService>();
builder.Services.AddSingleton<PrefixRuleEvaluator>();
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

// Live-push for the Sync module (the missing counterpart to ClipboardBroadcaster). It reuses the
// clipboard presence/routing table (ClipboardConnectionRegistry, registered below) so a recorded
// pair run — from the App, the VPS cron, or another of the user's machines — fans out to the user's
// OTHER live sessions over the WS they already hold, refreshing sync status in real time. Singleton,
// process-local; the registry it depends on is registered with the clipboard module.
builder.Services.AddSingleton<SyncBroadcaster>();

// Clipboard module (Plan 1 / F1a). Options bind from the "Clipboard" config section; the WS
// presence registry and the fan-out broadcaster are process-local singletons; the EF-backed
// history + settings stores are singletons over the DbContext factory like the other Ef*Store
// registrations (they resolve the ambient user per call through the singleton-safe accessor).
builder.Services.Configure<ClipboardOptions>(builder.Configuration.GetSection("Clipboard"));
builder.Services.AddSingleton<ClipboardConnectionRegistry>();
builder.Services.AddSingleton<ClipboardBroadcaster>();
builder.Services.AddSingleton<IClipboardHistoryStore, EfClipboardHistoryStore>();
builder.Services.AddSingleton<IClipboardSettingsStore, EfClipboardSettingsStore>();
// Lazy-blob store: image/file bytes on disk, outside the DB. Root from ClipboardOptions.BlobRoot,
// else a "clipboard-blobs" folder under the content root.
builder.Services.AddSingleton<IClipboardBlobStore>(sp =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ClipboardOptions>>().Value;
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    var root = string.IsNullOrWhiteSpace(opts.BlobRoot)
        ? System.IO.Path.Combine(env.ContentRootPath, "clipboard-blobs")
        : opts.BlobRoot;
    return new DiskClipboardBlobStore(root);
});

// Track C — plan/entitlements seam. Today's impl returns everything unlocked (∩ the user's
// toggles). SWAP: replace DefaultEntitlementsService with PlanBasedEntitlementsService here (one
// line) when plan gating (Free/PRO) goes live; nothing downstream changes.
builder.Services.AddSingleton<IEntitlementsService, DefaultEntitlementsService>();

// §D-1 — the external cron-trigger runner. Singleton over the DbContext factory (cross-user
// queries) + the run-lock + the module registry; it executes every due, uncovered pair when the
// external scheduler hits /api/sync/run-due.
builder.Services.AddSingleton<CronSyncRunner>();

// Calendar v2 — the replica/prefix-rule runner shares the same external trigger as the pair
// cron. Always server-side: the App never executes the replica engine, so no lease gating.
builder.Services.AddSingleton<ReplicaSyncRunner>();

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

// NOTE: schema is NOT applied at startup. Production migrations run via `efbundle` as the
// postgres superuser at deploy time (the app's DB role is CRUD-only, no DDL). The test host
// (ServerTestFactory) builds its SQLite schema with EnsureCreated(). See the deploy spec.

// Honour the Azure App Service reverse-proxy forwarded headers FIRST, before any middleware
// that reads the client IP or scheme — the per-IP magic-link rate limiter and UseHttpsRedirection
// both depend on RemoteIpAddress / Request.Scheme reflecting the real client, not the proxy. See
// the ForwardedHeadersOptions registration above for why clearing the known-proxy list is safe on
// App Service (the front-end overwrites X-Forwarded-* and is the only ingress).
//
// UsePathBase FIRST (before forwarded headers and any routing), so the app serves correctly under
// nginx's /zync/ prefix. Empty in local dev and tests (Server:PathBase defaults to ""), so those
// hosts are unaffected and routes resolve at the root.
var pathBase = builder.Configuration.GetSection("Server").Get<ServerOptions>()?.PathBase ?? string.Empty;
if (!string.IsNullOrWhiteSpace(pathBase))
    app.UsePathBase(pathBase);

app.UseForwardedHeaders();

// Per-request correlation id. Reuse an upstream X-Request-Id if present (else ASP.NET's per-request
// TraceIdentifier), echo it back in the response header, and push it into the logging scope so every
// log line emitted while handling this request carries RequestId — turning a pile of independent log
// lines into one traceable request. Placed early so it covers the whole pipeline.
app.Use(async (context, next) =>
{
    var requestId = context.Request.Headers["X-Request-Id"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(requestId))
        requestId = context.TraceIdentifier;
    context.Response.Headers["X-Request-Id"] = requestId;

    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ZyncMaster.Request");
    using (logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = requestId }))
        await next();
});

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

// WebSockets must be enabled before the endpoints that accept an upgrade (/ws/clipboard) and
// before auth so the upgrade request is authenticated like any other request.
//
// KeepAliveInterval makes the framework send a periodic ping on each open socket. This is what
// detects a HALF-OPEN clipboard socket (peer vanished without a Close frame, e.g. laptop slept or
// network dropped): the ping send eventually faults, the next ReceiveAsync in ClipboardHub throws a
// WebSocketException, the receive loop returns, and the /ws/clipboard finally block evicts the gone
// device from ClipboardConnectionRegistry and re-broadcasts presence. Without it the server keeps a
// phantom-online device forever and clients sit in ReceiveAsync — the root of the presence flicker.
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15),
    // The missing-PONG deadline: without it a silent half-open client (slept laptop, no RST) lingers in
    // ReceiveAsync and stays phantom-online in the registry until the 10-min lease. With it, the dead
    // socket aborts within ~15s, the receive loop returns, and the finally block evicts + re-broadcasts.
    KeepAliveTimeout = TimeSpan.FromSeconds(15),
});

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

// FIX 5 — /health now verifies the database connection, returning 503 when the DB is unreachable so
// an orchestrator/probe sees an unhealthy instance instead of a misleading 200 while every real
// request would fail. The check is deliberately CHEAP (Database.CanConnectAsync, which issues a
// trivial connectivity probe and not a query) to stay friendly to F1/cold-start: it adds one light
// round-trip, not a schema or data scan. Any exception (timeout, transient transport) is treated as
// unhealthy rather than bubbling a 500.
app.MapGet("/health", async (IDbContextFactory<ZyncMasterDbContext> dbFactory, CancellationToken ct) =>
{
    bool dbUp;
    try
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        dbUp = await db.Database.CanConnectAsync(ct);
    }
    catch
    {
        dbUp = false;
    }

    return dbUp
        ? Results.Ok(new { status = "ok", db = "up" })
        : Results.Json(new { status = "degraded", db = "down" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapDeviceEndpoints();
app.MapConnectEndpoints();
app.MapIdentityConnectEndpoints();
app.MapIdentityMagicLinkEndpoints();
app.MapCalendarConnectEndpoints();
app.MapCalendarV2Endpoints();
app.MapPrefixRuleEndpoints();
app.MapSyncEndpoints();
app.MapSyncRunDueEndpoints();
app.MapPanelEndpoints();
app.MapPairEndpoints();
app.MapPairApprovalEndpoints();
app.MapEntitlementEndpoints();
app.MapClipboardEndpoints();

app.Run();

public partial class Program { }
