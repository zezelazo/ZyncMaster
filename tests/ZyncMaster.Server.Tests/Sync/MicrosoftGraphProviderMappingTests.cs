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
        public FuncHandler(string json) => _json = json;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_json) });
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

        records.Select(r => r.Id).Should().BeEquivalentTo(new[] { "keep" });
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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReadWindow_rejects_blank_calendar_id(string calendarId)
    {
        var provider = ProviderReturning("""{ "value": [] }""");

        await provider.Invoking(p => p.ReadWindowAsync(calendarId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)))
            .Should().ThrowAsync<ArgumentException>();
    }
}
