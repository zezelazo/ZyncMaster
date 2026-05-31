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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZyncMaster.Core;
using ZyncMaster.Graph;

namespace ZyncMaster.Server.Tests.E2E;

// Full-flow E2E backbone. Runs the WHOLE server (real Program composition, real EF on
// SQLite, real cookie + ApiKey auth) and drives it exactly the way the panel/client do —
// multi-step journeys against the live HTTP surface, asserting the user-visible outcome at
// each step.
//
// The ONLY doubles are at the network boundary the server cannot reach in a test:
//   * IMicrosoftTokenService — a switchable fake so /connect -> /connect/callback produces a
//     known identity (and a second sign-in can produce a DIFFERENT user against the same host).
//   * The Graph reader/writer (ProviderRegistry) and the device-sync calendar target — recording
//     fakes that capture the account ref each call resolved and the events mirrored, so a
//     journey can assert that a push/run actually mirrored the caller's events into the
//     caller's destination account. Everything else (stores, scoping, validation, auth) is real.
//
// This harness is deliberately distinct from the single-endpoint unit tests and from
// CrossUserIsolationTests: the methods here read as narrative journey steps.
internal sealed class E2EHarness : IDisposable
{
    public ServerTestFactory Inner { get; }
    public WebApplicationFactory<Program> Factory { get; }
    public SwitchableIdentity Identity { get; } = new();

    // Per-pair-run capture: the destination account refs the writer was resolved with, the
    // source account refs the reader was resolved with, and every batch of events mirrored.
    public List<string?> WriterAccountRefs { get; } = new();
    public List<string?> ReaderAccountRefs { get; } = new();
    public List<IReadOnlyList<AppointmentRecord>> MirroredBatches { get; } = new();

    // Per device-sync capture: the account ref the /api/sync/calendar target was built for.
    public List<string> SyncTargetAccountRefs { get; } = new();

    // Events the recording reader hands back on the next /run (source side of an online pair).
    public List<AppointmentRecord> ReaderWindow { get; } = new();

    public E2EHarness()
    {
        Inner = new ServerTestFactory();
        var writerRefs = WriterAccountRefs;
        var readerRefs = ReaderAccountRefs;
        var batches = MirroredBatches;
        var syncRefs = SyncTargetAccountRefs;
        var readerWindow = ReaderWindow;

        Factory = Inner.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMicrosoftTokenService>();
            services.AddSingleton<IMicrosoftTokenService>(Identity);

            services.RemoveAll<ProviderRegistry>();
            services.AddSingleton(new ProviderRegistry(
                readerFactory: accountRef => { readerRefs.Add(accountRef); return new RecordingReader(readerWindow); },
                writerFactory: accountRef => { writerRefs.Add(accountRef); return new RecordingWriter(batches); }));

            services.RemoveAll<Func<string, ICalendarTarget>>();
            services.AddSingleton<Func<string, ICalendarTarget>>(_ => accountRef =>
            {
                syncRefs.Add(accountRef);
                return new RecordingTarget();
            });
        }));
    }

    // ---- Journey steps -----------------------------------------------------------------

    // Signs in as the given identity through the real OAuth flow; returns a cookie client that
    // rides the panel session on every later call, exactly like a signed-in browser.
    public async Task<HttpClient> SignInAsync(string subject, string email, string display, string refreshToken = "rt")
    {
        Identity.Use(subject, email, display, refreshToken);
        return await CookieAuthHelper.SignInAsync(Factory);
    }

    // The ZyncMaster user id for an identity (idempotent upsert, same row the callback created).
    public async Task<string> UserIdAsync(string subject, string email, string display)
    {
        var users = Factory.Services.GetRequiredService<IUserStore>();
        var row = await users.UpsertAsync("microsoft", subject, email, display);
        return row.Id;
    }

    // Binds a device to an explicit user id and returns its plaintext api key. Mirrors what
    // the pairing flow ultimately persists, but lets a journey jump straight to "device owns a key".
    public async Task<string> AddDeviceForUserAsync(string userId, string name = "Laptop")
    {
        var devices = Factory.Services.GetRequiredService<IDeviceStore>();
        var key = ApiKeyGenerator.Generate();
        await devices.AddAsync(new Device
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            Name = name,
            ApiKeyHash = ApiKeyHasher.Hash(key),
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        return key;
    }

    // An api-key client representing a paired device.
    public HttpClient DeviceClient(string apiKey)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    public void Dispose()
    {
        Factory.Dispose();
        Inner.Dispose();
    }

    // ---- Shared request bodies + assertions --------------------------------------------

    public static object OnlinePairBody(string name, string srcAccountRef, string dstAccountRef, int intervalMin = 15) => new
    {
        name,
        source = new { provider = "MicrosoftGraph", accountRef = srcAccountRef, calendarId = "src-cal", calendarName = "Source" },
        destination = new { provider = "MicrosoftGraph", accountRef = dstAccountRef, calendarId = "dst-cal", calendarName = "Destination" },
        intervalMin,
    };

    public static object DevicePairBody(string name, string dstAccountRef, int intervalMin = 15) => new
    {
        name,
        source = new { provider = "OutlookCom", calendarId = "outlook", calendarName = "Outlook (this PC)" },
        destination = new { provider = "MicrosoftGraph", accountRef = dstAccountRef, calendarId = "dst-cal", calendarName = "Destination" },
        intervalMin,
    };

    public static AppointmentRecord Event(string id)
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        return new AppointmentRecord
        {
            Id = id,
            Subject = id,
            StartOffset = start,
            EndOffset = start.AddHours(1),
            StartTimeZoneId = "UTC",
        };
    }

    public static async Task<string> CreatePairAsync(HttpClient client, object body)
    {
        var resp = await client.PostAsJsonAsync("/api/pairs", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "creating a pair should succeed");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    // ---- Recording doubles -------------------------------------------------------------

    // Switchable so the same host can sign in user A, then user B, each getting their own user
    // row + connected account through the real callback.
    public sealed class SwitchableIdentity : IMicrosoftTokenService
    {
        public string Subject { get; private set; } = "oid";
        public string Upn { get; private set; } = "user@test";
        public string DisplayName { get; private set; } = "User";
        public string RefreshToken { get; private set; } = "rt";

        public void Use(string subject, string upn, string display, string refreshToken)
        {
            Subject = subject;
            Upn = upn;
            DisplayName = display;
            RefreshToken = refreshToken;
        }

        public Task<TokenResult> ExchangeCodeAsync(string code, CancellationToken ct = default) =>
            Task.FromResult(new TokenResult
            {
                AccessToken = "at",
                RefreshToken = RefreshToken,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
                UserPrincipalName = Upn,
                Subject = Subject,
                Email = Upn,
                DisplayName = DisplayName,
            });

        public Task<TokenResult> ExchangeIdentityCodeAsync(string code, CancellationToken ct = default) =>
            ExchangeCodeAsync(code, ct);

        public Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
            Task.FromResult(new TokenResult
            {
                AccessToken = "at",
                RefreshToken = refreshToken,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
            });
    }

    private sealed class RecordingWriter : ICalendarWriter
    {
        private readonly List<IReadOnlyList<AppointmentRecord>> _batches;
        public RecordingWriter(List<IReadOnlyList<AppointmentRecord>> batches) => _batches = batches;

        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarOption>>(new[]
            {
                new CalendarOption { Id = "dst-cal", DisplayName = "Destination", IsDefault = true },
            });

        public Task<MirrorResult> MirrorAsync(
            string calendarId, IReadOnlyList<AppointmentRecord> records, int reminderMinutes,
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
        {
            _batches.Add(records);
            return Task.FromResult(new MirrorResult { Created = records.Count });
        }
    }

    private sealed class RecordingReader : ICalendarReader
    {
        private readonly List<AppointmentRecord> _window;
        public RecordingReader(List<AppointmentRecord> window) => _window = window;

        public Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AppointmentRecord>>(_window.ToList());
    }

    // Device-sync calendar target: a default calendar + a no-op create so /api/sync/calendar
    // resolves to a calendar id and mirrors without hitting Graph.
    private sealed class RecordingTarget : ICalendarTarget
    {
        public Task<IReadOnlyList<CalendarTargetInfo>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarTargetInfo>>(new[]
            {
                new CalendarTargetInfo { Id = "cal-default", DisplayName = "Calendar", IsDefault = true },
            });

        public Task<CalendarTargetInfo> CreateCalendarAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(new CalendarTargetInfo { Id = "n", DisplayName = name });

        public Task<IReadOnlyDictionary<string, ExistingEventLookup>> FindByExternalIdsAsync(
            string calendarId, IReadOnlyList<string> externalIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ExistingEventLookup>>(
                new Dictionary<string, ExistingEventLookup>(StringComparer.Ordinal));

        public Task<string> CreateEventAsync(string calendarId, EventDraft draft, CancellationToken ct = default) =>
            Task.FromResult("evt");

        public Task UpdateEventAsync(string eventId, EventDraft draft, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteEventAsync(string eventId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ManagedEventRef>> ListManagedInWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ManagedEventRef>>(Array.Empty<ManagedEventRef>());
    }
}
