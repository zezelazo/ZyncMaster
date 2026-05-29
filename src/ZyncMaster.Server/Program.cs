using Microsoft.Extensions.Options;
using ZyncMaster.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Server"));
builder.Services.AddDataProtection();

builder.Services.AddHttpClient<IMicrosoftTokenService, MicrosoftTokenService>();
builder.Services.AddHttpClient("graph");

builder.Services.AddSingleton<IDeviceStore, InMemoryDeviceStore>();
builder.Services.AddSingleton<ISyncStateStore, InMemorySyncStateStore>();
builder.Services.AddSingleton<ISecretProvider, ConfigurationSecretProvider>();
builder.Services.AddSingleton<IConnectedAccountStore, DataProtectionConnectedAccountStore>();
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
builder.Services.AddSingleton<ISyncPairStore, InMemorySyncPairStore>();

builder.Services.AddAuthentication("ApiKey").AddApiKeyAuth();
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

app.Run();

public partial class Program { }
