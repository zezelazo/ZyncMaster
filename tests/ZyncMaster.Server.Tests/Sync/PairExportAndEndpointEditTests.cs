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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZyncMaster.Core;
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// Covers F1 (Graph-source .txt export) and F2 (editing source/destination on PATCH /api/pairs).
// Every external boundary (Graph read/write, account store) is a fake; no network, no Outlook.
public class PairExportAndEndpointEditTests : IClassFixture<ServerTestFactory>
{
    private readonly ServerTestFactory _factory;

    public PairExportAndEndpointEditTests(ServerTestFactory factory) => _factory = factory;

    // A reader that returns a fixed set of records, so the .txt is deterministic.
    private sealed class FakeReader : ICalendarReader
    {
        public IReadOnlyList<AppointmentRecord> Records = Array.Empty<AppointmentRecord>();
        public string? LastCalendarId;
        public DateTimeOffset LastFrom;
        public DateTimeOffset LastTo;
        public bool LastPreserveLocalTime;
        public Func<Exception>? ThrowFactory;
        public List<CalendarOption> Calendars = new();

        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarOption>>(Calendars);

        public Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
            CancellationToken ct = default, bool preserveLocalTime = false)
        {
            LastCalendarId = calendarId;
            LastFrom = fromUtc;
            LastTo = toUtc;
            LastPreserveLocalTime = preserveLocalTime;
            if (ThrowFactory is not null)
                throw ThrowFactory();
            return Task.FromResult(Records);
        }
    }

    private sealed class FakeWriter : ICalendarWriter
    {
        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarOption>>(new[]
            {
                new CalendarOption { Id = "cal1", DisplayName = "Primary", IsDefault = true, Owner = "me@test" },
            });
        public Task<CalendarOption> CreateCalendarAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(new CalendarOption { Id = "n", DisplayName = name });
        public Task<MirrorResult> MirrorAsync(
            string calendarId, IReadOnlyList<AppointmentRecord> records, int reminderMinutes,
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default, string pairId = "") =>
            Task.FromResult(new MirrorResult());
    }

    private WebApplicationFactory<Program> Build(FakeReader reader)
        => _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMicrosoftTokenService>();
                services.AddSingleton<IMicrosoftTokenService>(
                    new CookieAuthHelper.FakeIdentityTokenService { Upn = "" });

                // Seam registry: a Graph endpoint resolves to our fake reader/writer; OutlookCom
                // still resolves to null reader (ProviderRegistry switches on provider), matching prod.
                var writer = new FakeWriter();
                services.RemoveAll<ProviderRegistry>();
                services.AddSingleton(new ProviderRegistry(
                    _ => reader,
                    _ => writer));

                services.RemoveAll<IConnectedAccountStore>();
                services.AddSingleton<IConnectedAccountStore>(_ =>
                {
                    var store = new DataProtectionConnectedAccountStore(DataProtectionProvider.Create("tests"));
                    store.SetAsync("default", "rt").GetAwaiter().GetResult();
                    return store;
                });

                services.RemoveAll<ISyncPairStore>();
                services.AddSingleton<ISyncPairStore, InMemorySyncPairStore>();
            });
        });

    private static Task<HttpClient> AuthedClientAsync(WebApplicationFactory<Program> factory) =>
        CookieAuthHelper.SignInAsync(factory);

    // Graph→Graph pair (distinct calendars in the same account so origin != destination passes).
    private static object GraphPairBody(string srcCal = "srcCal", string dstCal = "cal1") => new
    {
        name = "Graph pair",
        source = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = srcCal, calendarName = "Src" },
        destination = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = dstCal, calendarName = "Primary" },
        intervalMin = 15,
    };

    private static object ComSourcePairBody() => new
    {
        name = "Com pair",
        source = new { provider = "OutlookCom", calendarId = "local", calendarName = "Outlook (this PC)" },
        destination = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "cal1", calendarName = "Primary" },
        intervalMin = 15,
    };

    private static async Task<string> CreatePairAsync(HttpClient client, object body)
    {
        var create = await client.PostAsJsonAsync("/api/pairs", body);
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    // ---------------- F1: Graph-source .txt export ----------------

    [Fact]
    public async Task Export_requires_cookie()
    {
        var reader = new FakeReader();
        var factory = Build(reader);
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/pairs/anything/export-source-txt",
            new { year = 2026, month = 6, includeCancelled = true });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Export_unknown_pair_returns_404()
    {
        var reader = new FakeReader();
        var factory = Build(reader);
        var client = await AuthedClientAsync(factory);

        var resp = await client.PostAsJsonAsync("/api/pairs/missing/export-source-txt",
            new { year = 2026, month = 6, includeCancelled = true });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Export_com_source_returns_409_no_server_reader()
    {
        var reader = new FakeReader();
        var factory = Build(reader);
        var client = await AuthedClientAsync(factory);

        var id = await CreatePairAsync(client, ComSourcePairBody());

        var resp = await client.PostAsJsonAsync($"/api/pairs/{id}/export-source-txt",
            new { year = 2026, month = 6, includeCancelled = true });
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("no_server_reader");
    }

    [Fact]
    public async Task Export_graph_source_returns_simple_txt_identical_to_formatter()
    {
        var reader = new FakeReader
        {
            Records = new[]
            {
                new AppointmentRecord
                {
                    Start = new DateTime(2026, 6, 10, 9, 30, 0),
                    Duration = 90,
                    Subject = "Standup",
                    OrganizerName = "Ana",
                    OrganizerEmail = "ana@test",
                },
                new AppointmentRecord
                {
                    Start = new DateTime(2026, 6, 12, 0, 0, 0),
                    IsAllDay = true,
                    Subject = "Holiday",
                    OrganizerName = "HR",
                },
            },
        };
        var factory = Build(reader);
        var client = await AuthedClientAsync(factory);

        var id = await CreatePairAsync(client, GraphPairBody());

        var resp = await client.PostAsJsonAsync($"/api/pairs/{id}/export-source-txt",
            new { year = 2026, month = 6, includeCancelled = true });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");

        var txt = await resp.Content.ReadAsStringAsync();
        // Byte-identical to the shared Simple formatter — the single source of truth.
        txt.Should().Be(SimpleAppointmentFormatter.Format(reader.Records));
        txt.Should().Contain("2026-06-10 | 09:30 | 1h 30m | Standup | Ana <ana@test>");
        txt.Should().Contain("2026-06-12 | All day | All day | Holiday | HR");

        // The Graph window is the requested month padded one day on each side so a boundary event
        // in any earth offset is included; the endpoint then trims to the user's local month. The
        // export reads in preserveLocalTime mode so the .txt shows each event's local clock time.
        reader.LastCalendarId.Should().Be("srcCal");
        reader.LastFrom.Should().Be(new DateTimeOffset(2026, 5, 31, 0, 0, 0, TimeSpan.Zero));
        reader.LastTo.Should().Be(new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero));
        reader.LastPreserveLocalTime.Should().BeTrue();
    }

    [Fact]
    public async Task Export_renders_non_utc_event_in_local_time_and_keeps_it_in_month()
    {
        // An event whose Start carries a NON-UTC local clock time (10:00 in a UTC-5 zone). In the
        // export path the reader runs in preserveLocalTime mode, so Start is the LOCAL wall-clock
        // time the user sees — the .txt must show 10:00, never the 15:00 UTC instant. The event also
        // sits on the first day of the month, proving the local-month filter keeps boundary events.
        var localStart = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.FromHours(-5));
        var reader = new FakeReader
        {
            Records = new[]
            {
                new AppointmentRecord
                {
                    // Start is the LOCAL clock time (10:00), as preserveLocalTime yields.
                    Start = localStart.DateTime,
                    Duration = 60,
                    Subject = "Lima morning",
                    OrganizerName = "Zeze",
                    OrganizerEmail = "z@test",
                    StartOffset = localStart,
                    StartTimeZoneId = "SA Pacific Standard Time",
                },
            },
        };
        var factory = Build(reader);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        var resp = await client.PostAsJsonAsync($"/api/pairs/{id}/export-source-txt",
            new { year = 2026, month = 6, includeCancelled = true });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var txt = await resp.Content.ReadAsStringAsync();

        // Local time (10:00), not the UTC instant (15:00) — consistent with CalExport COM.
        txt.Should().Contain("2026-06-01 | 10:00 | 1h 00m | Lima morning | Zeze <z@test>");
        txt.Should().NotContain("15:00");
        // The reader was asked to preserve local time, and the window padded around the month.
        reader.LastPreserveLocalTime.Should().BeTrue();
    }

    [Fact]
    public async Task Export_excludes_cancelled_when_includeCancelled_false()
    {
        var reader = new FakeReader
        {
            Records = new[]
            {
                new AppointmentRecord { Start = new DateTime(2026, 6, 1, 8, 0, 0), Duration = 60, Subject = "Keep", OrganizerName = "A" },
                new AppointmentRecord { Start = new DateTime(2026, 6, 2, 8, 0, 0), Duration = 60, Subject = "Drop", OrganizerName = "A", IsCancelled = true },
            },
        };
        var factory = Build(reader);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        var resp = await client.PostAsJsonAsync($"/api/pairs/{id}/export-source-txt",
            new { year = 2026, month = 6, includeCancelled = false });

        var txt = await resp.Content.ReadAsStringAsync();
        txt.Should().Contain("Keep");
        txt.Should().NotContain("Drop");
        txt.Should().NotContain("CANCELADO");
    }

    [Fact]
    public async Task Export_includes_cancelled_marker_when_true()
    {
        var reader = new FakeReader
        {
            Records = new[]
            {
                new AppointmentRecord { Start = new DateTime(2026, 6, 2, 8, 0, 0), Duration = 60, Subject = "Drop", OrganizerName = "A", IsCancelled = true },
            },
        };
        var factory = Build(reader);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        var resp = await client.PostAsJsonAsync($"/api/pairs/{id}/export-source-txt",
            new { year = 2026, month = 6, includeCancelled = true });

        (await resp.Content.ReadAsStringAsync()).Should().EndWith("| CANCELADO");
    }

    [Fact]
    public async Task Export_empty_calendar_returns_empty_body()
    {
        var reader = new FakeReader { Records = Array.Empty<AppointmentRecord>() };
        var factory = Build(reader);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        var resp = await client.PostAsJsonAsync($"/api/pairs/{id}/export-source-txt",
            new { year = 2026, month = 6, includeCancelled = true });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public async Task Export_invalid_month_returns_400(int month)
    {
        var reader = new FakeReader();
        var factory = Build(reader);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        var resp = await client.PostAsJsonAsync($"/api/pairs/{id}/export-source-txt",
            new { year = 2026, month, includeCancelled = true });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Export_transient_read_failure_returns_503()
    {
        var reader = new FakeReader { ThrowFactory = () => new GraphRequestException("429 throttled", isTransient: true) };
        var factory = Build(reader);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        var resp = await client.PostAsJsonAsync($"/api/pairs/{id}/export-source-txt",
            new { year = 2026, month = 6, includeCancelled = true });

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // ---------------- F2: edit source/destination ----------------

    [Fact]
    public async Task Patch_changing_destination_resets_run_state()
    {
        var reader = new FakeReader();
        var factory = Build(reader);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        // Seed a LastRunUtc / LastResult so we can prove the change resets them.
        var store = factory.Services.GetRequiredService<ISyncPairStore>();
        var existing = await store.GetAsync(id);
        await store.UpdateAsync(existing! with
        {
            LastRunUtc = DateTimeOffset.UtcNow,
            LastResult = new MirrorResult { Created = 5 },
        });

        var patch = await client.PatchAsJsonAsync($"/api/pairs/{id}", new
        {
            destination = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "cal-new", calendarName = "New" },
        });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await patch.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("destination").GetProperty("calendarId").GetString().Should().Be("cal-new");
        // Run state cleared because the destination changed.
        (root.TryGetProperty("lastRunUtc", out var lr) && lr.ValueKind != JsonValueKind.Null).Should().BeFalse();
        (root.TryGetProperty("lastResult", out var rr) && rr.ValueKind != JsonValueKind.Null).Should().BeFalse();

        // The id is preserved (edit-in-place, not recreate).
        root.GetProperty("id").GetString().Should().Be(id);
    }

    // FIX D — a PATCH re-target must NOT race a run. A run persists the WHOLE pair row, so a PATCH
    // concurrent with a run could be silently clobbered (losing the new destination -> orphans in the
    // old destination forever). The PATCH now takes the per-pair run-lock, so while a run holds it the
    // PATCH answers 409 run_in_progress and the destination is left UNCHANGED — never half-applied.
    [Fact]
    public async Task Patch_while_run_in_progress_returns_409_and_does_not_change_destination()
    {
        var reader = new FakeReader();
        var factory = Build(reader);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        // Simulate an in-progress run by holding the pair's run-lock (the same lock /run and /push
        // take). The production EfSyncRunLock is registered; acquire it the way the endpoints do.
        var runLock = factory.Services.GetRequiredService<ISyncRunLock>();
        await using var handle = await runLock.TryAcquireAsync(id, TimeSpan.FromMinutes(8), owner: "run");
        handle.Should().NotBeNull("the test must hold the lock to simulate an in-flight run");

        var patch = await client.PatchAsJsonAsync($"/api/pairs/{id}", new
        {
            destination = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "cal-new", calendarName = "New" },
        });

        patch.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await patch.Content.ReadAsStringAsync()).Should().Contain("run_in_progress");

        // The destination must be UNTOUCHED — the re-target was rejected wholesale, not half-applied.
        var store = factory.Services.GetRequiredService<ISyncPairStore>();
        var after = await store.GetAsync(id);
        after!.Destination.CalendarId.Should().Be("cal1", "the rejected PATCH must not change the destination");
    }

    // FIX D — once the run releases the lock, the very same re-target PATCH succeeds and persists.
    [Fact]
    public async Task Patch_succeeds_after_run_lock_released()
    {
        var reader = new FakeReader();
        var factory = Build(reader);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        var runLock = factory.Services.GetRequiredService<ISyncRunLock>();
        var handle = await runLock.TryAcquireAsync(id, TimeSpan.FromMinutes(8), owner: "run");
        handle.Should().NotBeNull();
        await handle!.DisposeAsync(); // run finished, lock released

        var patch = await client.PatchAsJsonAsync($"/api/pairs/{id}", new
        {
            destination = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "cal-new", calendarName = "New" },
        });

        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await patch.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("destination").GetProperty("calendarId").GetString().Should().Be("cal-new");
    }

    [Fact]
    public async Task Patch_name_only_preserves_run_state_and_endpoints()
    {
        var reader = new FakeReader();
        var factory = Build(reader);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        var store = factory.Services.GetRequiredService<ISyncPairStore>();
        var existing = await store.GetAsync(id);
        await store.UpdateAsync(existing! with { LastResult = new MirrorResult { Created = 3 } });

        var patch = await client.PatchAsJsonAsync($"/api/pairs/{id}", new { name = "Just renamed" });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await patch.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("name").GetString().Should().Be("Just renamed");
        // Endpoints unchanged → run state preserved.
        root.GetProperty("lastResult").GetProperty("created").GetInt32().Should().Be(3);
        root.GetProperty("destination").GetProperty("calendarId").GetString().Should().Be("cal1");
    }

    [Fact]
    public async Task Patch_to_same_source_and_destination_returns_400()
    {
        var reader = new FakeReader();
        var factory = Build(reader);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        // Set the source calendar equal to the existing destination ("cal1") → self-mirror.
        var patch = await client.PatchAsJsonAsync($"/api/pairs/{id}", new
        {
            source = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "cal1", calendarName = "Primary" },
        });

        patch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await patch.Content.ReadAsStringAsync()).Should().Contain("same_source_destination");
    }

    [Fact]
    public async Task Patch_to_unknown_destination_account_returns_400()
    {
        var reader = new FakeReader();
        var factory = Build(reader);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        var patch = await client.PatchAsJsonAsync($"/api/pairs/{id}", new
        {
            destination = new { provider = "MicrosoftGraph", accountRef = "ghost@test", calendarId = "cal-x", calendarName = "X" },
        });

        patch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await patch.Content.ReadAsStringAsync()).Should().Contain("destination.accountRef");
    }

    [Fact]
    public async Task Patch_with_invalid_source_provider_returns_400()
    {
        var reader = new FakeReader();
        var factory = Build(reader);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        var patch = await client.PatchAsJsonAsync($"/api/pairs/{id}", new
        {
            source = new { provider = "Bogus", calendarId = "x", calendarName = "X" },
        });

        patch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_changing_source_keeps_id_and_resets_run_state()
    {
        var reader = new FakeReader();
        var factory = Build(reader);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        var store = factory.Services.GetRequiredService<ISyncPairStore>();
        var existing = await store.GetAsync(id);
        await store.UpdateAsync(existing! with { LastRunUtc = DateTimeOffset.UtcNow, LastResult = new MirrorResult { Updated = 2 } });

        var patch = await client.PatchAsJsonAsync($"/api/pairs/{id}", new
        {
            source = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "src-new", calendarName = "NewSrc" },
        });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await patch.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("id").GetString().Should().Be(id);
        root.GetProperty("source").GetProperty("calendarId").GetString().Should().Be("src-new");
        (root.TryGetProperty("lastResult", out var rr) && rr.ValueKind != JsonValueKind.Null).Should().BeFalse();
    }
}
