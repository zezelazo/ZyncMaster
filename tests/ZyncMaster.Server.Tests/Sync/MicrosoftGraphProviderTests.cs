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

public class MicrosoftGraphProviderTests
{
    private sealed class StubTokenProvider : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            Task.FromResult("token");
    }

    private static class StubHandler
    {
        public static HttpClient ClientReturning(string json)
        {
            var handler = new FuncHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
            return new HttpClient(handler);
        }
    }

    private sealed class FuncHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _fn;
        public List<string> RequestedUrls { get; } = new();
        public FuncHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            return Task.FromResult(_fn(request, ct));
        }
    }

    private sealed class FakeCalendarTarget : ICalendarTarget
    {
        public IReadOnlyList<CalendarTargetInfo> Calendars { get; set; } = Array.Empty<CalendarTargetInfo>();
        public List<EventDraft> Creates { get; } = new();

        public Task<IReadOnlyList<CalendarTargetInfo>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult(Calendars);
        public Task<CalendarTargetInfo> CreateCalendarAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(new CalendarTargetInfo { Id = "new", DisplayName = name });
        public Task<IReadOnlyDictionary<string, ExistingEventLookup>> FindByExternalIdsAsync(
            string calendarId, IReadOnlyList<string> externalIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ExistingEventLookup>>(
                new Dictionary<string, ExistingEventLookup>());
        public Task<string> CreateEventAsync(string calendarId, EventDraft draft, CancellationToken ct = default)
        {
            Creates.Add(draft);
            return Task.FromResult("evt-" + draft.ExternalId);
        }
        public Task UpdateEventAsync(string eventId, EventDraft draft, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteEventAsync(string eventId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ManagedEventRef>> ListManagedInWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ManagedEventRef>>(Array.Empty<ManagedEventRef>());
    }

    private static MicrosoftGraphProvider Build(HttpClient readHttp, ICalendarTarget target) =>
        new(readHttp, new StubTokenProvider(), target);

    [Fact]
    public async Task ReadWindow_maps_events_to_appointment_records()
    {
        const string json = """
        {
          "value": [
            {
              "id": "evt1",
              "iCalUId": "ical-1",
              "subject": "Standup",
              "bodyPreview": "Daily sync",
              "isAllDay": false,
              "isCancelled": false,
              "organizer": { "emailAddress": { "name": "Alice", "address": "alice@test" } },
              "start": { "dateTime": "2026-05-29T09:00:00.0000000", "timeZone": "UTC" },
              "end":   { "dateTime": "2026-05-29T09:30:00.0000000", "timeZone": "UTC" }
            }
          ]
        }
        """;
        var provider = Build(StubHandler.ClientReturning(json), new FakeCalendarTarget());

        var records = await provider.ReadWindowAsync("cal1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(14));

        records.Should().HaveCount(1);
        var r = records[0];
        r.Id.Should().Be("ical-1");
        r.Subject.Should().Be("Standup");
        r.Description.Should().Be("Daily sync");
        r.OrganizerEmail.Should().Be("alice@test");
        r.Duration.Should().Be(30);
        r.StartOffset.Should().Be(new DateTimeOffset(2026, 5, 29, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task ReadWindow_falls_back_to_event_id_when_no_icaluid()
    {
        const string json = """
        { "value": [ { "id": "evt2", "subject": "X",
          "start": { "dateTime": "2026-05-29T09:00:00.0000000", "timeZone": "UTC" },
          "end":   { "dateTime": "2026-05-29T10:00:00.0000000", "timeZone": "UTC" } } ] }
        """;
        var provider = Build(StubHandler.ClientReturning(json), new FakeCalendarTarget());

        var records = await provider.ReadWindowAsync("cal1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        records.Should().ContainSingle().Which.Id.Should().Be("evt2");
    }

    [Fact]
    public async Task ReadWindow_follows_pagination()
    {
        var page1 = """
        { "@odata.nextLink": "https://graph.microsoft.com/v1.0/next-page",
          "value": [ { "id": "a", "subject": "A",
            "start": { "dateTime": "2026-05-29T09:00:00.0000000", "timeZone": "UTC" },
            "end":   { "dateTime": "2026-05-29T10:00:00.0000000", "timeZone": "UTC" } } ] }
        """;
        var page2 = """
        { "value": [ { "id": "b", "subject": "B",
            "start": { "dateTime": "2026-05-30T09:00:00.0000000", "timeZone": "UTC" },
            "end":   { "dateTime": "2026-05-30T10:00:00.0000000", "timeZone": "UTC" } } ] }
        """;
        var calls = 0;
        var handler = new FuncHandler((_, _) =>
        {
            var body = calls++ == 0 ? page1 : page2;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        var provider = Build(new HttpClient(handler), new FakeCalendarTarget());

        var records = await provider.ReadWindowAsync("cal1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(2));

        records.Select(r => r.Id).Should().BeEquivalentTo(new[] { "a", "b" });
        handler.RequestedUrls.Should().HaveCount(2);
        handler.RequestedUrls[1].Should().Be("https://graph.microsoft.com/v1.0/next-page");
    }

    [Fact]
    public async Task ReadWindow_throws_on_non_success()
    {
        var handler = new FuncHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom"),
        });
        var provider = Build(new HttpClient(handler), new FakeCalendarTarget());

        await provider.Invoking(p => p.ReadWindowAsync("cal1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)))
            .Should().ThrowAsync<GraphRequestException>();
    }

    [Fact]
    public async Task ListCalendars_projects_target_info_to_options()
    {
        var target = new FakeCalendarTarget
        {
            Calendars = new[]
            {
                new CalendarTargetInfo { Id = "c1", DisplayName = "Primary", IsDefault = true, Owner = "me@test" },
            },
        };
        var provider = Build(StubHandler.ClientReturning("{}"), target);

        var options = await provider.ListCalendarsAsync();

        options.Should().ContainSingle();
        options[0].Id.Should().Be("c1");
        options[0].IsDefault.Should().BeTrue();
        options[0].Owner.Should().Be("me@test");
    }

    [Fact]
    public async Task Mirror_creates_via_target_and_returns_counts()
    {
        var target = new FakeCalendarTarget();
        var provider = Build(StubHandler.ClientReturning("{}"), target);

        var start = DateTimeOffset.UtcNow.AddDays(1);
        var records = new List<AppointmentRecord>
        {
            new() { Id = "x", Subject = "X", StartOffset = start, EndOffset = start.AddHours(1), StartTimeZoneId = "UTC" },
        };

        var result = await provider.MirrorAsync("cal1", records, 30, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(14));

        result.Created.Should().Be(1);
        target.Creates.Should().ContainSingle().Which.ExternalId.Should().Be("x");
    }
}

public class ProviderRegistryTests
{
    private sealed class StubTokenProvider : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            Task.FromResult("token");
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

    private ProviderRegistry Build(out List<string?> requestedAccounts)
    {
        var accounts = new List<string?>();
        requestedAccounts = accounts;
        return new ProviderRegistry(accountRef =>
        {
            accounts.Add(accountRef);
            return new MicrosoftGraphProvider(new HttpClient(), new StubTokenProvider(), new NoopTarget());
        });
    }

    [Fact]
    public void ResolveReader_returns_graph_provider_for_microsoft_graph()
    {
        var registry = Build(out _);
        var ep = new Endpoint { Provider = "MicrosoftGraph", AccountRef = "alice@test", CalendarId = "c" };

        registry.ResolveReader(ep).Should().BeOfType<MicrosoftGraphProvider>();
    }

    [Fact]
    public void ResolveReader_returns_null_for_outlook_com()
    {
        var registry = Build(out _);
        var ep = new Endpoint { Provider = "OutlookCom", AccountRef = null, CalendarId = "c" };

        registry.ResolveReader(ep).Should().BeNull();
    }

    [Fact]
    public void ResolveWriter_is_always_graph()
    {
        var registry = Build(out _);
        var ep = new Endpoint { Provider = "OutlookCom", AccountRef = "bob@test", CalendarId = "c" };

        registry.ResolveWriter(ep).Should().BeOfType<MicrosoftGraphProvider>();
    }

    [Fact]
    public void Resolve_passes_account_ref_to_factory()
    {
        var registry = Build(out var requested);
        var ep = new Endpoint { Provider = "MicrosoftGraph", AccountRef = "carol@test", CalendarId = "c" };

        registry.ResolveWriter(ep);

        requested.Should().ContainSingle().Which.Should().Be("carol@test");
    }
}
