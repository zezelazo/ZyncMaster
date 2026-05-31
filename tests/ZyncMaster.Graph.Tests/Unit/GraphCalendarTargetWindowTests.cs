using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.Graph.Tests;

public sealed class GraphCalendarTargetWindowTests
{
    private static readonly Guid PropGuid = new Guid("11111111-2222-3333-4444-555555555555");

    // Shape the class builds: "String {GUID-UPPER} Name CalImportSourceId"
    private static string ExtendedPropertyId(Guid g)
        => $"String {{{g.ToString("D").ToUpperInvariant()}}} Name CalImportSourceId";

    private sealed class FakeTokenProvider : IGraphTokenProvider
    {
        private readonly string _token;
        public FakeTokenProvider(string token) => _token = token;

        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult(_token);
    }

    // Returns a queued sequence of JSON bodies, one per request, 200 OK each.
    private sealed class QueuedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;
        public List<Uri> Requests { get; } = new List<Uri>();

        public QueuedHandler(params string[] bodies) => _bodies = new Queue<string>(bodies);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var body = _bodies.Count > 0 ? _bodies.Dequeue() : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string EventJson(string eventId, string? sourceId)
    {
        var prop = sourceId == null
            ? "[]"
            : $"[{{\"id\":\"{ExtendedPropertyId(PropGuid)}\",\"value\":\"{sourceId}\"}}]";
        return $"{{\"id\":\"{eventId}\",\"singleValueExtendedProperties\":{prop}}}";
    }

    [Fact]
    public async Task ListManagedInWindowAsync_FollowsPaging_ReturnsAllManagedEvents()
    {
        var page1 =
            "{\"value\":[" +
            EventJson("ev-1", "s1") + "," +
            EventJson("ev-2", "s2") +
            "],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/me/calendars/CAL/calendarView?$skiptoken=PAGE2\"}";
        var page2 =
            "{\"value\":[" +
            EventJson("ev-3", "s3") +
            "]}";

        var handler = new QueuedHandler(page1, page2);
        var sut     = new GraphCalendarTarget(new HttpClient(handler), new FakeTokenProvider("tok"), PropGuid);

        var from = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var result = await sut.ListManagedInWindowAsync("CAL", from, to);

        result.Should().HaveCount(3);
        result.Select(r => r.SourceId).Should().Equal("s1", "s2", "s3");
        result.Select(r => r.EventId).Should().Equal("ev-1", "ev-2", "ev-3");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListManagedInWindowAsync_SkipsEventsWithoutSourceId()
    {
        var page =
            "{\"value\":[" +
            EventJson("ev-1", "s1") + "," +
            EventJson("ev-2", null) +          // empty extended-properties array → skipped
            "]}";

        var handler = new QueuedHandler(page);
        var sut     = new GraphCalendarTarget(new HttpClient(handler), new FakeTokenProvider("tok"), PropGuid);

        var from = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var result = await sut.ListManagedInWindowAsync("CAL", from, to);

        result.Should().ContainSingle();
        result[0].SourceId.Should().Be("s1");
        result[0].EventId.Should().Be("ev-1");
    }

    [Fact]
    public async Task ListManagedInWindowAsync_FormatsWindowBoundsAsInvariantUtc()
    {
        var handler = new QueuedHandler("{\"value\":[]}");
        var sut     = new GraphCalendarTarget(new HttpClient(handler), new FakeTokenProvider("tok"), PropGuid);

        // A non-UTC offset must be normalized to UTC in the query.
        var from = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.FromHours(2)); // 06:00Z
        var to   = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        await sut.ListManagedInWindowAsync("CAL", from, to);

        var url = handler.Requests.Single().ToString();
        url.Should().Contain(Uri.EscapeDataString("2026-05-01T06:00:00Z"));
        url.Should().Contain(Uri.EscapeDataString("2026-06-01T00:00:00Z"));
    }

    // Fix 1 (BLOCKER) — the sweep's own enumeration must be truncation-proof too. If a
    // continuation page comes back 2xx with NO "value" collection, treating it as end-of-pages
    // would hand the sweep a short managed-event set and could mis-drive deletes. The
    // enumeration must abort with a transient instead.
    [Fact]
    public async Task ListManagedInWindowAsync_throws_transient_on_truncated_continuation_page()
    {
        var page1 =
            "{\"value\":[" + EventJson("ev-1", "s1") +
            "],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/me/calendars/CAL/calendarView?$skiptoken=PAGE2\"}";
        // Second page: a 2xx object with no "value" key at all.
        var malformed = "{\"unexpected\":\"x\"}";

        var handler = new QueuedHandler(page1, malformed);
        var sut     = new GraphCalendarTarget(new HttpClient(handler), new FakeTokenProvider("tok"), PropGuid);

        var from = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var act = async () => await sut.ListManagedInWindowAsync("CAL", from, to);

        var ex = (await act.Should().ThrowAsync<GraphRequestException>()).Which;
        ex.IsTransient.Should().BeTrue("a truncated sweep enumeration must be transient, not silently short");
    }

    // Fix 1 — the empty last page (`value: []`, no nextLink) is the normal terminator and must
    // NOT throw, even though a missing-value page does.
    [Fact]
    public async Task ListManagedInWindowAsync_terminates_normally_on_empty_last_page()
    {
        var page1 =
            "{\"value\":[" + EventJson("ev-1", "s1") +
            "],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/me/calendars/CAL/calendarView?$skiptoken=PAGE2\"}";
        var lastPage = "{\"value\":[]}";

        var handler = new QueuedHandler(page1, lastPage);
        var sut     = new GraphCalendarTarget(new HttpClient(handler), new FakeTokenProvider("tok"), PropGuid);

        var from = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var result = await sut.ListManagedInWindowAsync("CAL", from, to);

        result.Should().ContainSingle().Which.SourceId.Should().Be("s1");
    }

    // Fix 1 — a 2xx with a non-JSON body is a malformed response; SendJsonAsync converts the
    // parse failure into a transient GraphRequestException rather than a raw JsonReaderException
    // (which would classify as Fatal and could let the sweep proceed on a short set).
    [Fact]
    public async Task ListManagedInWindowAsync_throws_transient_on_non_json_body()
    {
        var handler = new QueuedHandler("<html>not json</html>");
        var sut     = new GraphCalendarTarget(new HttpClient(handler), new FakeTokenProvider("tok"), PropGuid);

        var from = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var act = async () => await sut.ListManagedInWindowAsync("CAL", from, to);

        var ex = (await act.Should().ThrowAsync<GraphRequestException>()).Which;
        ex.IsTransient.Should().BeTrue();
    }
}
