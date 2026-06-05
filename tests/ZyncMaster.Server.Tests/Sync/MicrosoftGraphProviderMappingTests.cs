using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Core;
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// Focused coverage of the calendarView reader's MapEvent / ParseGraphDateTime projection that
// the existing MicrosoftGraphProviderTests do not exercise: isCancelled mapping, all-day flag,
// negative-duration clamping, empty-id skip, non-UTC timezone offset application, unknown
// timezone fallback, the whitespace-calendarId guard, and the default body content.
public class MicrosoftGraphProviderMappingTests
{
    private sealed class StubTokenProvider : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            Task.FromResult("token");
    }

    private sealed class FuncHandler : HttpMessageHandler
    {
        private readonly string _json;
        public string? LastPreferHeader;
        public bool SawPreferHeader;
        public FuncHandler(string json) => _json = json;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            SawPreferHeader = request.Headers.TryGetValues("Prefer", out var values);
            LastPreferHeader = SawPreferHeader ? string.Join(",", values!) : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_json) });
        }
    }

    private sealed class NoopTarget : ICalendarTarget
    {
        public Task<IReadOnlyList<CalendarTargetInfo>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarTargetInfo>>(Array.Empty<CalendarTargetInfo>());
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

    private static MicrosoftGraphProvider ProviderReturning(string json) =>
        new(new HttpClient(new FuncHandler(json)), new StubTokenProvider(), new NoopTarget());

    private static Task<IReadOnlyList<AppointmentRecord>> ReadAsync(string json) =>
        ProviderReturning(json).ReadWindowAsync("cal1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7));

    [Fact]
    public async Task Maps_isCancelled_true()
    {
        const string json = """
        { "value": [ { "id": "c1", "subject": "Dropped", "isCancelled": true,
          "start": { "dateTime": "2026-05-29T09:00:00.0000000", "timeZone": "UTC" },
          "end":   { "dateTime": "2026-05-29T10:00:00.0000000", "timeZone": "UTC" } } ] }
        """;

        var records = await ReadAsync(json);

        records.Should().ContainSingle().Which.IsCancelled.Should().BeTrue();
    }

    [Fact]
    public async Task Maps_isAllDay_true()
    {
        const string json = """
        { "value": [ { "id": "a1", "subject": "Holiday", "isAllDay": true,
          "start": { "dateTime": "2026-05-29T00:00:00.0000000", "timeZone": "UTC" },
          "end":   { "dateTime": "2026-05-30T00:00:00.0000000", "timeZone": "UTC" } } ] }
        """;

        var records = await ReadAsync(json);

        var r = records.Should().ContainSingle().Subject;
        r.IsAllDay.Should().BeTrue();
        r.Duration.Should().Be(24 * 60);
    }

    [Fact]
    public async Task Clamps_negative_duration_to_zero_when_end_precedes_start()
    {
        const string json = """
        { "value": [ { "id": "n1", "subject": "Backwards",
          "start": { "dateTime": "2026-05-29T10:00:00.0000000", "timeZone": "UTC" },
          "end":   { "dateTime": "2026-05-29T09:00:00.0000000", "timeZone": "UTC" } } ] }
        """;

        var records = await ReadAsync(json);

        records.Should().ContainSingle().Which.Duration.Should().Be(0);
    }

    [Fact]
    public async Task Skips_events_with_empty_id()
    {
        const string json = """
        { "value": [
          { "id": "", "subject": "No id",
            "start": { "dateTime": "2026-05-29T09:00:00.0000000", "timeZone": "UTC" },
            "end":   { "dateTime": "2026-05-29T10:00:00.0000000", "timeZone": "UTC" } },
          { "id": "keep", "subject": "Kept",
            "start": { "dateTime": "2026-05-29T09:00:00.0000000", "timeZone": "UTC" },
            "end":   { "dateTime": "2026-05-29T10:00:00.0000000", "timeZone": "UTC" } } ] }
        """;

        var records = await ReadAsync(json);

        // The empty-id event is dropped; the kept one maps to its per-occurrence id (FIX 1).
        records.Should().ContainSingle().Which.Id.Should()
            .Be(OccurrenceId.For("keep", new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task Applies_named_timezone_offset_to_unspecified_dateTime()
    {
        // "Eastern Standard Time" is -05:00 in (standard) winter — Graph hands back a wall-clock
        // dateTime with no offset, the reader interprets it in the declared zone. (Use a Windows
        // zone id since the test host is Windows; January avoids DST ambiguity.)
        const string json = """
        { "value": [ { "id": "tz1", "subject": "Eastern",
          "start": { "dateTime": "2026-01-15T09:00:00.0000000", "timeZone": "Eastern Standard Time" },
          "end":   { "dateTime": "2026-01-15T10:00:00.0000000", "timeZone": "Eastern Standard Time" } } ] }
        """;

        var records = await ReadAsync(json);

        var r = records.Should().ContainSingle().Subject;
        r.StartOffset.Offset.Should().Be(TimeSpan.FromHours(-5));
        r.StartOffset.UtcDateTime.Should().Be(new DateTime(2026, 1, 15, 14, 0, 0, DateTimeKind.Utc));
        r.StartTimeZoneId.Should().Be("Eastern Standard Time");
    }

    [Fact]
    public async Task Falls_back_to_utc_for_an_unknown_timezone()
    {
        const string json = """
        { "value": [ { "id": "tz2", "subject": "Bogus zone",
          "start": { "dateTime": "2026-05-29T09:00:00.0000000", "timeZone": "Not/A/Real/Zone" },
          "end":   { "dateTime": "2026-05-29T10:00:00.0000000", "timeZone": "Not/A/Real/Zone" } } ] }
        """;

        var records = await ReadAsync(json);

        records.Should().ContainSingle().Which.StartOffset.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task Defaults_missing_body_and_organizer_to_empty()
    {
        const string json = """
        { "value": [ { "id": "m1", "subject": "Bare",
          "start": { "dateTime": "2026-05-29T09:00:00.0000000", "timeZone": "UTC" },
          "end":   { "dateTime": "2026-05-29T10:00:00.0000000", "timeZone": "UTC" } } ] }
        """;

        var records = await ReadAsync(json);

        var r = records.Should().ContainSingle().Subject;
        r.Description.Should().BeEmpty();
        r.OrganizerName.Should().BeEmpty();
        r.OrganizerEmail.Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_value_array_yields_no_records()
    {
        var records = await ReadAsync("""{ "value": [] }""");

        records.Should().BeEmpty();
    }

    // ── FIX 1 — recurring-series occurrences must NOT collapse onto one Id ──────────────────
    // calendarView expands a recurring series into N occurrences that all share the SAME iCalUId
    // (and the same series event id). Before the fix MapEvent set Id = iCalUId, so the N
    // occurrences collapsed onto ONE AppointmentRecord.Id — losing N-1 events and making the
    // downstream mirror UPDATE one destination event N times (orphaning the rest on the next run).
    [Fact]
    public async Task Recurring_occurrences_sharing_iCalUId_get_distinct_ids()
    {
        const string json = """
        { "value": [
          { "id": "occ-mon", "iCalUId": "SERIES-ABC", "subject": "Standup",
            "start": { "dateTime": "2026-06-01T09:00:00.0000000", "timeZone": "UTC" },
            "end":   { "dateTime": "2026-06-01T09:15:00.0000000", "timeZone": "UTC" } },
          { "id": "occ-tue", "iCalUId": "SERIES-ABC", "subject": "Standup",
            "start": { "dateTime": "2026-06-02T09:00:00.0000000", "timeZone": "UTC" },
            "end":   { "dateTime": "2026-06-02T09:15:00.0000000", "timeZone": "UTC" } },
          { "id": "occ-wed", "iCalUId": "SERIES-ABC", "subject": "Standup",
            "start": { "dateTime": "2026-06-03T09:00:00.0000000", "timeZone": "UTC" },
            "end":   { "dateTime": "2026-06-03T09:15:00.0000000", "timeZone": "UTC" } } ] }
        """;

        var records = await ReadAsync(json);

        records.Should().HaveCount(3);
        records.Select(r => r.Id).Distinct().Should().HaveCount(3,
            "each occurrence of a recurring series must map to its own distinct upsert id");
    }

    [Fact]
    public async Task Recurring_occurrence_id_is_stable_and_matches_the_COM_path()
    {
        // The same occurrence (same series id + same start) must resolve to the SAME id on every
        // run (idempotent upsert), AND must equal what the COM path (OccurrenceId.For) produces so
        // a Graph→Graph mirror and a COM→Graph mirror of the same source agree on the key.
        const string json = """
        { "value": [
          { "id": "occ-1", "iCalUId": "SERIES-XYZ", "subject": "Weekly",
            "start": { "dateTime": "2026-06-10T14:00:00.0000000", "timeZone": "UTC" },
            "end":   { "dateTime": "2026-06-10T15:00:00.0000000", "timeZone": "UTC" } } ] }
        """;

        var first = await ReadAsync(json);
        var second = await ReadAsync(json);

        var start = new DateTimeOffset(2026, 6, 10, 14, 0, 0, TimeSpan.Zero);
        var expected = OccurrenceId.For("SERIES-XYZ", start);

        first.Single().Id.Should().Be(expected);
        second.Single().Id.Should().Be(first.Single().Id, "the occurrence id must be stable across runs");
    }

    [Fact]
    public async Task Event_without_iCalUId_folds_the_event_id_with_the_start()
    {
        // No iCalUId -> stableId falls back to the Graph event id, still folded with the start so a
        // single (non-recurring) event keeps a deterministic, start-qualified key.
        const string json = """
        { "value": [
          { "id": "single-1", "subject": "One-off",
            "start": { "dateTime": "2026-06-11T08:00:00.0000000", "timeZone": "UTC" },
            "end":   { "dateTime": "2026-06-11T09:00:00.0000000", "timeZone": "UTC" } } ] }
        """;

        var records = await ReadAsync(json);

        var expected = OccurrenceId.For("single-1", new DateTimeOffset(2026, 6, 11, 8, 0, 0, TimeSpan.Zero));
        records.Single().Id.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReadWindow_rejects_blank_calendar_id(string calendarId)
    {
        var provider = ProviderReturning("""{ "value": [] }""");

        await provider.Invoking(p => p.ReadWindowAsync(calendarId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Sync_read_forces_utc_via_prefer_header()
    {
        // Default (sync/mirror) path: the Prefer:UTC header normalizes every event to UTC so the
        // destructive mirror reconciles against UTC-normalized destination events. Must NOT change.
        var handler = new FuncHandler("""{ "value": [] }""");
        var provider = new MicrosoftGraphProvider(new HttpClient(handler), new StubTokenProvider(), new NoopTarget());

        await provider.ReadWindowAsync("cal1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        handler.SawPreferHeader.Should().BeTrue();
        handler.LastPreferHeader.Should().Contain("outlook.timezone=\"UTC\"");
    }

    [Fact]
    public async Task Export_read_omits_prefer_header_to_keep_local_time()
    {
        // Export-to-.txt path (preserveLocalTime): the Prefer header is omitted so Graph returns
        // each event in its ORIGINAL declared zone, and Start ends up as the local clock time the
        // user sees — matching CalExport COM rather than UTC.
        var handler = new FuncHandler("""{ "value": [] }""");
        var provider = new MicrosoftGraphProvider(new HttpClient(handler), new StubTokenProvider(), new NoopTarget());

        await provider.ReadWindowAsync(
            "cal1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), preserveLocalTime: true);

        handler.SawPreferHeader.Should().BeFalse();
    }
}
