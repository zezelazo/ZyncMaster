using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.Graph.Tests;

// Covers the destination-cleanup support on GraphCalendarTarget: the second managed property
// (CalImportPairId) written on Create, and the windowless ListManagedByPairAsync enumeration.
public sealed class GraphCalendarTargetPairCleanupTests
{
    private static readonly Guid PropGuid = new Guid("11111111-2222-3333-4444-555555555555");

    private static string SourcePropId(Guid g) => $"String {{{g.ToString("D").ToUpperInvariant()}}} Name CalImportSourceId";
    private static string PairPropId(Guid g)   => $"String {{{g.ToString("D").ToUpperInvariant()}}} Name CalImportPairId";

    private sealed class FakeTokenProvider : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult("tok");
    }

    // Captures each request (method, uri, body) and replies with a queued 200 body.
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;
        public List<(HttpMethod Method, Uri Uri, string Body)> Requests { get; } = new();

        public CapturingHandler(params string[] bodies) => _bodies = new Queue<string>(bodies);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!, body));
            var reply = _bodies.Count > 0 ? _bodies.Dequeue() : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static string EventJson(string eventId, string sourceId, string pairId)
        => $"{{\"id\":\"{eventId}\",\"singleValueExtendedProperties\":[" +
           $"{{\"id\":\"{SourcePropId(PropGuid)}\",\"value\":\"{sourceId}\"}}," +
           $"{{\"id\":\"{PairPropId(PropGuid)}\",\"value\":\"{pairId}\"}}]}}";

    // An event that carries ONLY the source property (no CalImportPairId) — e.g. a foreign event
    // a mis-honored Graph $filter wrongly returned. The client-side guard must discard it.
    private static string EventJsonNoPairProp(string eventId, string sourceId)
        => $"{{\"id\":\"{eventId}\",\"singleValueExtendedProperties\":[" +
           $"{{\"id\":\"{SourcePropId(PropGuid)}\",\"value\":\"{sourceId}\"}}]}}";

    // An event with the pair property present but a DIFFERENT value than the queried pairId.
    private static string EventJsonWrongPair(string eventId, string sourceId, string otherPairId)
        => $"{{\"id\":\"{eventId}\",\"singleValueExtendedProperties\":[" +
           $"{{\"id\":\"{SourcePropId(PropGuid)}\",\"value\":\"{sourceId}\"}}," +
           $"{{\"id\":\"{PairPropId(PropGuid)}\",\"value\":\"{otherPairId}\"}}]}}";

    // An event with NO expanded singleValueExtendedProperties block at all.
    private static string EventJsonNoProps(string eventId)
        => $"{{\"id\":\"{eventId}\"}}";

    [Fact]
    public async Task CreateEventAsync_writes_both_source_and_pair_extended_properties()
    {
        var handler = new CapturingHandler("{\"id\":\"new-evt\"}");
        var sut = new GraphCalendarTarget(new HttpClient(handler), new FakeTokenProvider(), PropGuid);

        var start = new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero);
        var draft = new EventDraft
        {
            Subject = "Standup",
            Start = start,
            End = start.AddHours(1),
            TimeZoneId = "UTC",
            ExternalId = "src-123",
            PairId = "pair-abc",
        };

        await sut.CreateEventAsync("CAL", draft);

        var body = JObject.Parse(handler.Requests.Single().Body);
        var props = (JArray)body["singleValueExtendedProperties"]!;
        props.Should().HaveCount(2);

        var byId = props.ToDictionary(p => p["id"]!.Value<string>()!, p => p["value"]!.Value<string>()!);
        byId[SourcePropId(PropGuid)].Should().Be("src-123");
        byId[PairPropId(PropGuid)].Should().Be("pair-abc");
    }

    [Fact]
    public async Task CreateEventAsync_omits_pair_property_when_pairId_is_empty()
    {
        var handler = new CapturingHandler("{\"id\":\"new-evt\"}");
        var sut = new GraphCalendarTarget(new HttpClient(handler), new FakeTokenProvider(), PropGuid);

        var start = new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero);
        var draft = new EventDraft
        {
            Subject = "Standup",
            Start = start,
            End = start.AddHours(1),
            TimeZoneId = "UTC",
            ExternalId = "src-123",
            // PairId left empty (the account-less device /sync path).
        };

        await sut.CreateEventAsync("CAL", draft);

        var body = JObject.Parse(handler.Requests.Single().Body);
        var props = (JArray)body["singleValueExtendedProperties"]!;
        props.Should().ContainSingle();
        props[0]["id"]!.Value<string>().Should().Be(SourcePropId(PropGuid));
    }

    [Fact]
    public async Task ListManagedByPairAsync_filters_by_pairId_and_follows_paging()
    {
        var page1 =
            "{\"value\":[" +
            EventJson("ev-1", "s1", "pair-x") + "," +
            EventJson("ev-2", "s2", "pair-x") +
            "],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/me/calendars/CAL/events?$skiptoken=P2\"}";
        var page2 =
            "{\"value\":[" + EventJson("ev-3", "s3", "pair-x") + "]}";

        var handler = new CapturingHandler(page1, page2);
        var sut = new GraphCalendarTarget(new HttpClient(handler), new FakeTokenProvider(), PropGuid);

        var result = await sut.ListManagedByPairAsync("CAL", "pair-x");

        result.Select(r => r.EventId).Should().Equal("ev-1", "ev-2", "ev-3");
        result.Select(r => r.SourceId).Should().Equal("s1", "s2", "s3");
        handler.Requests.Should().HaveCount(2);

        // The $filter targets the PAIR property NAME and the pair id value — never enumerates the
        // whole calendar, so events without the property (user's own / other pairs') never match.
        // The whole filter clause is URL-escaped as a unit, so the property id appears with its
        // literal spaces/braces inside the query rather than per-character percent-encoding.
        var firstUrl = handler.Requests[0].Uri.ToString();
        firstUrl.Should().Contain("CalImportPairId");
        firstUrl.Should().Contain("pair-x");
        firstUrl.Should().Contain("/events?");
        firstUrl.Should().NotContain("calendarView", "the pair cleanup enumeration is windowless");
    }

    [Fact]
    public async Task ListManagedByPairAsync_discards_events_that_do_not_prove_pair_ownership()
    {
        // The server returns a mix that a perfectly-honored $filter would never produce, simulating
        // a mis-honored filter: only the genuinely-owned event must survive into the delete set.
        var page =
            "{\"value\":[" +
            EventJson("ev-owned", "s-owned", "pair-x") + "," +     // genuine: keep
            EventJsonNoPairProp("ev-nopair", "s-nopair") + "," +   // missing pair prop: discard
            EventJsonWrongPair("ev-other", "s-other", "pair-y") + "," + // different pair: discard
            EventJsonNoProps("ev-noprops") +                       // not expanded at all: discard
            "]}";

        var handler = new CapturingHandler(page);
        var sut = new GraphCalendarTarget(new HttpClient(handler), new FakeTokenProvider(), PropGuid);

        var result = await sut.ListManagedByPairAsync("CAL", "pair-x");

        // ONLY the event that carries CalImportPairId == pair-x is eligible for deletion.
        result.Select(r => r.EventId).Should().Equal("ev-owned");
        result.Single().SourceId.Should().Be("s-owned");
    }

    [Fact]
    public async Task ListManagedByPairAsync_throws_transient_on_truncated_continuation_page()
    {
        var page1 =
            "{\"value\":[" + EventJson("ev-1", "s1", "pair-x") +
            "],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/me/calendars/CAL/events?$skiptoken=P2\"}";
        var malformed = "{\"unexpected\":\"x\"}";

        var handler = new CapturingHandler(page1, malformed);
        var sut = new GraphCalendarTarget(new HttpClient(handler), new FakeTokenProvider(), PropGuid);

        var act = async () => await sut.ListManagedByPairAsync("CAL", "pair-x");

        var ex = (await act.Should().ThrowAsync<GraphRequestException>()).Which;
        ex.IsTransient.Should().BeTrue();
    }
}
