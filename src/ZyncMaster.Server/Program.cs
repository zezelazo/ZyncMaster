using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using ZyncMaster.Server;
using ZyncMaster.Server.Data;

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
builder.Services.AddScoped<DeviceService>();
builder.Services.AddScoped<SyncService>();

builder.Services.AddSingleton<Func<string, ZyncMaster.Graph.ICalendarTarget>>(sp => upn =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("graph");
    var tokens = sp.GetRequiredService<IMicrosoftTokenService>();
    var accounts = sp.GetRequiredService<IConnectedAccountStore>();
    var opts = sp.GetRequiredService<IOptions<ServerOptions>>().Value;
    var provider = new ServerGraphTokenProvider(tokens, accounts, upn);
    return new ZyncMaster.Graph.GraphCalendarTarget(http, provider, Guid.Parse(opts.ExtendedPropertyGuid));
});

// Per-account Microsoft Graph provider (reader + writer). A null/empty accountRef
// normalizes to the connected-account store's "default" key.
builder.Services.AddSingleton<Func<string?, MicrosoftGraphProvider>>(sp => accountRef =>
{
    var upn = string.IsNullOrWhiteSpace(accountRef) ? "" : accountRef;
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("graph");
    var readHttp = sp.GetRequiredService<IHttpClientFactory>().CreateClient("graph");
    var tokens = sp.GetRequiredService<IMicrosoftTokenService>();
    var accounts = sp.GetRequiredService<IConnectedAccountStore>();
    var opts = sp.GetRequiredService<IOptions<ServerOptions>>().Value;
    var tokenProvider = new ServerGraphTokenProvider(tokens, accounts, upn);
    var target = new ZyncMaster.Graph.GraphCalendarTarget(http, tokenProvider, Guid.Parse(opts.ExtendedPropertyGuid));
    return new MicrosoftGraphProvider(readHttp, tokenProvider, target);
});
builder.Services.AddSingleton<ProviderRegistry>();
builder.Services.AddSingleton<ISyncPairStore, EfSyncPairStore>();

// ApiKey stays the default scheme so device endpoints (/api/*) keep working as before;
// the Cookie scheme is added alongside it for the human panel. Cookies are HttpOnly,
// SameSite=Lax, sliding-expiration, and Secure only over https (so the http test host
// still round-trips the cookie).
builder.Services.AddAuthentication(AuthSchemes.ApiKey)
    .AddApiKeyAuth()
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

app.UseAuthentication();
app.UseAuthorization();

// Web panel static files. The canonical Liquid Glass UI lives at the repo root in ui/ and is
// the single source of truth shared with the desktop App (which bundles the same folder). We
// serve it directly from there rather than copying into wwwroot so there is no drift: in a
// normal run the content root is src/ZyncMaster.Server, so ../../ui resolves to the repo ui/.
// The same path holds under the WebApplicationFactory test host, which uses the project dir as
// its content root. If that folder is missing (e.g. a published layout that copied ui/ into
// wwwroot/) we fall back to the default web root so the panel still serves.
var uiRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "ui"));
var panelFileProvider = Directory.Exists(uiRoot)
    ? new PhysicalFileProvider(uiRoot)
    : app.Environment.WebRootFileProvider;

var defaultFilesOptions = new DefaultFilesOptions { FileProvider = panelFileProvider };
defaultFilesOptions.DefaultFileNames.Clear();
defaultFilesOptions.DefaultFileNames.Add("index.html");
app.UseDefaultFiles(defaultFilesOptions);
app.UseStaticFiles(new StaticFileOptions { FileProvider = panelFileProvider });

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapDeviceEndpoints();
app.MapConnectEndpoints();
app.MapSyncEndpoints();
app.MapPanelEndpoints();
app.MapPairEndpoints();
app.MapPairApprovalEndpoints();

app.Run();

public partial class Program { }
