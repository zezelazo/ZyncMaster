using SyncMaster.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IDeviceStore, InMemoryDeviceStore>();
builder.Services.AddSingleton<ISyncStateStore, InMemorySyncStateStore>();
builder.Services.AddScoped<DeviceService>();
builder.Services.AddAuthentication("ApiKey").AddApiKeyAuth();
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapDeviceEndpoints();

app.Run();

public partial class Program { }
