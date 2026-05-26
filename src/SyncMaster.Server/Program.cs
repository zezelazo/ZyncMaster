using SyncMaster.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("Server"));
builder.Services.AddDataProtection();

builder.Services.AddSingleton<IDeviceStore, InMemoryDeviceStore>();
builder.Services.AddSingleton<ISyncStateStore, InMemorySyncStateStore>();
builder.Services.AddSingleton<ISecretProvider, ConfigurationSecretProvider>();
builder.Services.AddSingleton<IConnectedAccountStore, DataProtectionConnectedAccountStore>();
builder.Services.AddScoped<DeviceService>();
builder.Services.AddHttpClient<IMicrosoftTokenService, MicrosoftTokenService>();
builder.Services.AddAuthentication("ApiKey").AddApiKeyAuth();
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapDeviceEndpoints();
app.MapConnectEndpoints();

app.Run();

public partial class Program { }
