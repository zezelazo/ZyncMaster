using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZyncMaster.Core;
using ZyncMaster.Server.Data;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// Track C gate: the cron run-due path must SKIP a user's due pairs when that user's
// cloudFallbackSync entitlement is off, and run them as before when it is on (the default). Reuses
// the same run-due harness shape as SyncRunDueEndpointTests; the only extra is seeding the
// UserToggleRow that drives the gate.
public sealed class CronSyncRunnerEntitlementGateTests
{
    private const string Secret = "cron-secret-value";

    private sealed class RecordingReader : ICalendarReader
    {
        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarOption>>(Array.Empty<CalendarOption>());

        public Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
            CancellationToken ct = default, bool preserveLocalTime = false) =>
            Task.FromResult<IReadOnlyList<AppointmentRecord>>(Array.Empty<AppointmentRecord>());
    }

    private sealed class RecordingWriter : ICalendarWriter
    {
        public readonly List<string> Mirrored = new();

        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarOption>>(Array.Empty<CalendarOption>());

        public Task<CalendarOption> CreateCalendarAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(new CalendarOption { Id = "new", DisplayName = name });

        public Task<MirrorResult> MirrorAsync(
            string calendarId, IReadOnlyList<AppointmentRecord> records, int reminderMinutes,
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default, string pairId = "")
        {
            lock (Mirrored) Mirrored.Add(calendarId);
            return Task.FromResult(new MirrorResult { Created = 1 });
        }
    }

    private static WebApplicationFactory<Program> Build(RecordingWriter writer) =>
        new ServerTestFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Server:CronTriggerSecret", Secret);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ProviderRegistry>();
                services.AddSingleton(new ProviderRegistry(
                    readerFactory: _ => new RecordingReader(),
                    writerFactory: _ => writer));
            });
        });

    private static async Task SeedAsync(
        WebApplicationFactory<Program> factory, string userId, string destCalendarId, bool? cloudFallbackToggle)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();

        if (await db.Users.FindAsync(userId) is null)
        {
            db.Users.Add(new UserRow
            {
                Id = userId,
                Provider = "local",
                Subject = userId,
                CreatedUtc = DateTimeOffset.UtcNow,
            });
        }

        var protector = scope.ServiceProvider
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("ZyncMaster.RefreshToken");
        if (!await db.ConnectedAccounts.AnyAsync(a => a.UserId == userId))
        {
            db.ConnectedAccounts.Add(new ConnectedAccountRow
            {
                Id = userId + "|default",
                UserId = userId,
                Provider = "MicrosoftGraph",
                AccountRef = "default",
                EncryptedRefreshToken = protector.Protect("rt"),
                ConnectedUtc = DateTimeOffset.UtcNow,
            });
        }

        if (cloudFallbackToggle is not null)
        {
            db.UserToggles.Add(new UserToggleRow { UserId = userId, CloudFallbackSync = cloudFallbackToggle.Value });
        }

        db.SyncPairs.Add(new SyncPairRow
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            Name = "Pair-" + destCalendarId,
            SourceJson = TestEndpointJson.Serialize(new Endpoint
            {
                Provider = "MicrosoftGraph", AccountRef = "default", CalendarId = "src-" + destCalendarId,
            }),
            DestinationJson = TestEndpointJson.Serialize(new Endpoint
            {
                Provider = "MicrosoftGraph", AccountRef = "default", CalendarId = destCalendarId,
            }),
            IntervalMin = 15,
            State = "active",
            LastRunUtc = null,
        });
        await db.SaveChangesAsync();
    }

    private static HttpRequestMessage RunDue()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/sync/run-due")
        {
            Content = JsonContent.Create(new { }),
        };
        req.Headers.Add(SyncRunDueEndpoints.SecretHeader, Secret);
        return req;
    }

    private static async Task<(int ran, int skipped, int failed)> ParseSummary(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (
            doc.RootElement.GetProperty("ran").GetInt32(),
            doc.RootElement.GetProperty("skipped").GetInt32(),
            doc.RootElement.GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task Cloud_fallback_off_skips_the_users_pairs()
    {
        var writer = new RecordingWriter();
        using var factory = Build(writer);
        await SeedAsync(factory, "u-off", "dst-off", cloudFallbackToggle: false);

        var resp = await factory.CreateClient().SendAsync(RunDue());
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var (ran, skipped, _) = await ParseSummary(resp);
        ran.Should().Be(0);
        skipped.Should().Be(1);
        writer.Mirrored.Should().BeEmpty("the user opted out of cloud fallback");
    }

    [Fact]
    public async Task Cloud_fallback_on_runs_the_pair()
    {
        var writer = new RecordingWriter();
        using var factory = Build(writer);
        await SeedAsync(factory, "u-on", "dst-on", cloudFallbackToggle: true);

        var resp = await factory.CreateClient().SendAsync(RunDue());
        var (ran, _, _) = await ParseSummary(resp);
        ran.Should().Be(1);
        writer.Mirrored.Should().Contain("dst-on");
    }

    [Fact]
    public async Task Default_no_toggle_runs_the_pair_unchanged()
    {
        var writer = new RecordingWriter();
        using var factory = Build(writer);
        await SeedAsync(factory, "u-default", "dst-default", cloudFallbackToggle: null);

        var resp = await factory.CreateClient().SendAsync(RunDue());
        var (ran, _, _) = await ParseSummary(resp);
        ran.Should().Be(1, "default is everything unlocked; behaviour is unchanged");
        writer.Mirrored.Should().Contain("dst-default");
    }

    [Fact]
    public async Task Gate_is_isolated_per_user()
    {
        var writer = new RecordingWriter();
        using var factory = Build(writer);
        await SeedAsync(factory, "u-blocked", "dst-blocked", cloudFallbackToggle: false);
        await SeedAsync(factory, "u-allowed", "dst-allowed", cloudFallbackToggle: null);

        var resp = await factory.CreateClient().SendAsync(RunDue());
        var (ran, skipped, _) = await ParseSummary(resp);
        ran.Should().Be(1);
        skipped.Should().Be(1);
        writer.Mirrored.Should().Contain("dst-allowed");
        writer.Mirrored.Should().NotContain("dst-blocked");
    }
}
