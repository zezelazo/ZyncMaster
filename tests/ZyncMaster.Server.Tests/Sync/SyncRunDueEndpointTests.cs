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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZyncMaster.Core;
using ZyncMaster.Server.Data;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// End-to-end tests for the external cron-trigger endpoint POST /api/sync/run-due (plan §D-1/§D-2).
// The endpoint enumerates due, uncovered pairs across ALL users and runs each server-side under
// its owner's identity. The run-lock is the production EfSyncRunLock; the calendar reader/writer
// are deterministic doubles so we observe exactly which pairs ran.
public sealed class SyncRunDueEndpointTests
{
    private const string Secret = "cron-secret-value";

    // A reader that returns a fixed set, and a writer that records which calendars it mirrored. A
    // calendar id in FailFor throws to prove one pair's failure does not abort the batch.
    private sealed class RecordingReader : ICalendarReader
    {
        public Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AppointmentRecord>>(Array.Empty<AppointmentRecord>());
    }

    private sealed class RecordingWriter : ICalendarWriter
    {
        public readonly List<string> Mirrored = new();
        public HashSet<string> FailFor { get; init; } = new();

        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarOption>>(Array.Empty<CalendarOption>());

        public Task<MirrorResult> MirrorAsync(
            string calendarId, IReadOnlyList<AppointmentRecord> records, int reminderMinutes,
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
        {
            lock (Mirrored) Mirrored.Add(calendarId);
            if (FailFor.Contains(calendarId))
                throw new InvalidOperationException("boom");
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

    // Seeds a user, that user's connected account (so token resolution succeeds), and a pair owned
    // by the user. Optionally seeds an active device lease for the user, and lets the caller mark
    // a pair as already-run (LastRunUtc) or COM-pinned.
    private static async Task<string> SeedAsync(
        WebApplicationFactory<Program> factory,
        string userId,
        string destCalendarId,
        int intervalMin = 15,
        DateTimeOffset? lastRunUtc = null,
        bool comPinned = false,
        bool withActiveLease = false,
        string state = "active")
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

        // A connected account for this user so AccountAwareGraphTokenProvider resolves a token.
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

        if (withActiveLease)
        {
            db.Devices.Add(new DeviceRow
            {
                Id = Guid.NewGuid().ToString("N"),
                UserId = userId,
                Name = "App",
                ApiKeyHash = "x",
                CreatedUtc = DateTimeOffset.UtcNow,
                LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(10),
            });
        }

        var source = comPinned
            ? new Endpoint { Provider = "OutlookCom", AccountRef = "default", CalendarId = "com-src" }
            : new Endpoint { Provider = "MicrosoftGraph", AccountRef = "default", CalendarId = "src-" + destCalendarId };

        var pairId = Guid.NewGuid().ToString("N");
        db.SyncPairs.Add(new SyncPairRow
        {
            Id = pairId,
            UserId = userId,
            Name = "Pair-" + destCalendarId,
            SourceJson = TestEndpointJson.Serialize(source),
            DestinationJson = TestEndpointJson.Serialize(new Endpoint
            {
                Provider = "MicrosoftGraph",
                AccountRef = "default",
                CalendarId = destCalendarId,
            }),
            IntervalMin = intervalMin,
            State = state,
            LastRunUtc = lastRunUtc,
        });
        await db.SaveChangesAsync();
        return pairId;
    }

    private static HttpRequestMessage RunDue(string? secret)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/sync/run-due")
        {
            Content = JsonContent.Create(new { }),
        };
        if (secret is not null)
            req.Headers.Add(SyncRunDueEndpoints.SecretHeader, secret);
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
    public async Task Wrong_secret_returns_401()
    {
        var writer = new RecordingWriter();
        using var factory = Build(writer);
        await SeedAsync(factory, "u1", "dst1");

        var resp = await factory.CreateClient().SendAsync(RunDue("nope"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        writer.Mirrored.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_secret_returns_401()
    {
        var writer = new RecordingWriter();
        using var factory = Build(writer);
        var resp = await factory.CreateClient().SendAsync(RunDue(null));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Disabled_when_secret_unconfigured_returns_503()
    {
        var writer = new RecordingWriter();
        using var factory = new ServerTestFactory().WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ProviderRegistry>();
                s.AddSingleton(new ProviderRegistry(_ => new RecordingReader(), _ => writer));
            }));

        var resp = await factory.CreateClient().SendAsync(RunDue("anything"));
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Runs_due_pair_without_active_lease()
    {
        var writer = new RecordingWriter();
        using var factory = Build(writer);
        await SeedAsync(factory, "u1", "dst1");

        var resp = await factory.CreateClient().SendAsync(RunDue(Secret));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var (ran, _, failed) = await ParseSummary(resp);
        ran.Should().Be(1);
        failed.Should().Be(0);
        writer.Mirrored.Should().Contain("dst1");
    }

    [Fact]
    public async Task Skips_pair_whose_user_has_active_device_lease()
    {
        var writer = new RecordingWriter();
        using var factory = Build(writer);
        await SeedAsync(factory, "u1", "dst1", withActiveLease: true);

        var resp = await factory.CreateClient().SendAsync(RunDue(Secret));
        var (ran, skipped, _) = await ParseSummary(resp);
        ran.Should().Be(0);
        skipped.Should().Be(1);
        writer.Mirrored.Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_com_pinned_pair()
    {
        var writer = new RecordingWriter();
        using var factory = Build(writer);
        await SeedAsync(factory, "u1", "dst1", comPinned: true);

        var resp = await factory.CreateClient().SendAsync(RunDue(Secret));
        var (ran, skipped, _) = await ParseSummary(resp);
        ran.Should().Be(0);
        skipped.Should().Be(1);
        writer.Mirrored.Should().BeEmpty();
    }

    [Fact]
    public async Task Is_idempotent_second_immediate_call_does_not_rerun()
    {
        var writer = new RecordingWriter();
        using var factory = Build(writer);
        await SeedAsync(factory, "u1", "dst1", intervalMin: 60);
        var client = factory.CreateClient();

        var first = await ParseSummary(await client.SendAsync(RunDue(Secret)));
        first.ran.Should().Be(1);

        // Immediately again: the pair was just run, so its interval (60m) has not elapsed -> not due.
        var second = await ParseSummary(await client.SendAsync(RunDue(Secret)));
        second.ran.Should().Be(0);

        writer.Mirrored.Count(c => c == "dst1").Should().Be(1, "the pair must run only once");
    }

    [Fact]
    public async Task One_failing_pair_does_not_abort_the_others()
    {
        var writer = new RecordingWriter { FailFor = { "dst-bad" } };
        using var factory = Build(writer);
        await SeedAsync(factory, "u1", "dst-bad");
        await SeedAsync(factory, "u2", "dst-good");

        var resp = await factory.CreateClient().SendAsync(RunDue(Secret));
        var (ran, _, failed) = await ParseSummary(resp);

        ran.Should().Be(1, "the good pair still ran");
        failed.Should().Be(1, "the bad pair failed but did not abort the batch");
        writer.Mirrored.Should().Contain("dst-good");
    }

    [Fact]
    public async Task Skips_paused_pair()
    {
        var writer = new RecordingWriter();
        using var factory = Build(writer);
        await SeedAsync(factory, "u1", "dst1", state: "paused");

        var resp = await factory.CreateClient().SendAsync(RunDue(Secret));
        var (ran, _, _) = await ParseSummary(resp);
        ran.Should().Be(0);
        writer.Mirrored.Should().BeEmpty();
    }
}
