using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZyncMaster.Graph;
using ZyncMaster.Server.Data;
using Xunit;

namespace ZyncMaster.Server.Tests.Security;

// Proves the ApiKey handler attaches BOTH the deviceId and the device's userId claim, and
// that the userId claim then scopes the EF stores: a device owned by a non-default user
// resolves THAT user's connected account during a sync.
public class ApiKeyUserClaimTests
{
    private sealed class FakeTarget : ICalendarTarget
    {
        public Task<IReadOnlyList<CalendarTargetInfo>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarTargetInfo>>(new[]
            {
                new CalendarTargetInfo { Id = "cal1", DisplayName = "Primary", IsDefault = true },
            });
        public Task<CalendarTargetInfo> CreateCalendarAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(new CalendarTargetInfo { Id = "n", DisplayName = name });
        public Task<IReadOnlyDictionary<string, ExistingEventLookup>> FindByExternalIdsAsync(
            string calendarId, IReadOnlyList<string> externalIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ExistingEventLookup>>(new Dictionary<string, ExistingEventLookup>());
        public Task<string> CreateEventAsync(string calendarId, EventDraft draft, CancellationToken ct = default) =>
            Task.FromResult("id");
        public Task UpdateEventAsync(string eventId, EventDraft draft, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteEventAsync(string eventId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ManagedEventRef>> ListManagedInWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ManagedEventRef>>(Array.Empty<ManagedEventRef>());
    }

    private static WebApplicationFactory<Program> Build() =>
        new ServerTestFactory().WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<Func<string, ICalendarTarget>>();
            services.AddSingleton<Func<string, ICalendarTarget>>(_ => _ => new FakeTarget());
        }));

    // Seeds a user + a device owned by that user + that user's connected account, all
    // through the shared SQLite DbContext.
    private static async Task<string> SeedUserDeviceAndAccountAsync(
        WebApplicationFactory<Program> factory, string apiKey)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();

        var userId = "u-" + Guid.NewGuid().ToString("N");
        db.Users.Add(new UserRow
        {
            Id = userId,
            Provider = "microsoft",
            Subject = userId,
            Email = "owner@test",
            DisplayName = "Owner",
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        db.Devices.Add(new DeviceRow
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            Name = "Phone",
            ApiKeyHash = ApiKeyHasher.Hash(apiKey),
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        // A connected account that only exists for THIS user. The "default" user has none.
        var protector = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()
            .CreateProtector("ZyncMaster.RefreshToken");
        db.ConnectedAccounts.Add(new ConnectedAccountRow
        {
            Id = userId + "|default",
            UserId = userId,
            Provider = "MicrosoftGraph",
            AccountRef = "default",
            EncryptedRefreshToken = protector.Protect("rt"),
            ConnectedUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return userId;
    }

    [Fact]
    public async Task Sync_resolves_the_device_owners_account_via_userId_claim()
    {
        var factory = Build();
        var key = ApiKeyGenerator.Generate();
        await SeedUserDeviceAndAccountAsync(factory, key);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        var resp = await client.PostAsJsonAsync("/api/sync/calendar", new { events = Array.Empty<object>() });

        // If the userId claim were missing the EF account store would scope to "default"
        // (which has no account) and the endpoint would return 409 no_connected_account.
        // Reaching 200 proves the claim scoped the lookup to the device owner.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("created").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Sync_without_owner_account_returns_no_connected_account()
    {
        // Same device owner but NO connected account seeded for that user: the userId claim
        // scopes to the owner, finds nothing, and the endpoint reports no account.
        var factory = new ServerTestFactory().WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<Func<string, ICalendarTarget>>();
            services.AddSingleton<Func<string, ICalendarTarget>>(_ => _ => new FakeTarget());
        }));

        var key = ApiKeyGenerator.Generate();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
            var userId = "u-" + Guid.NewGuid().ToString("N");
            db.Users.Add(new UserRow { Id = userId, Provider = "microsoft", Subject = userId, CreatedUtc = DateTimeOffset.UtcNow });
            db.Devices.Add(new DeviceRow
            {
                Id = Guid.NewGuid().ToString("N"),
                UserId = userId,
                Name = "Phone",
                ApiKeyHash = ApiKeyHasher.Hash(key),
                CreatedUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        var resp = await client.PostAsJsonAsync("/api/sync/calendar", new { events = Array.Empty<object>() });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("no_connected_account");
    }
}
