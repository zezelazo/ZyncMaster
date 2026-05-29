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
}
