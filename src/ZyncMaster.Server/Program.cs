using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZyncMaster.Server;
using ZyncMaster.Server.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Server"));

// EF Core persistence. A DbContextFactory (singleton) backs the stores so they can be
// shared by the singleton composition root; a scoped DbContext is also registered for
// the Data Protection key ring. The connection string falls back to a LocalDB-style dev
// default; WS-D wires deployment + migration-on-startup.
var connectionString = builder.Configuration.GetConnectionString("ZyncMasterDb")
    ?? "Server=(localdb)\\MSSQLLocalDB;Database=ZyncMaster;Trusted_Connection=True;MultipleActiveResultSets=true";

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

app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapDeviceEndpoints();
app.MapConnectEndpoints();
app.MapSyncEndpoints();
app.MapPanelEndpoints();
app.MapPairEndpoints();
app.MapPairApprovalEndpoints();

app.Run();

public partial class Program { }
