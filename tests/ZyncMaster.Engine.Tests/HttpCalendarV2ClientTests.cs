using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Engine.Tests;

// HttpCalendarV2Client is a RAW-JSON pass-through REST client for /api/calendar/* (Calendar v2).
// These tests pin the transport contract: bearer header, exact method+path, body passthrough,
// verbatim response body, and SyncClientException (with status) on non-2xx. Mirrors the
// StubHandler pattern of HttpPairsClientTests.
public class HttpCalendarV2ClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string?> Bodies { get; } = new();
        private readonly HttpStatusCode _status;
        private readonly string _responseBody;

        public StubHandler(HttpStatusCode status, string responseBody)
        {
            _status = status;
            _responseBody = responseBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (HttpCalendarV2Client client, StubHandler stub) Create(
        HttpStatusCode status = HttpStatusCode.OK, string body = "{}")
    {
        var stub = new StubHandler(status, body);
        return (new HttpCalendarV2Client(new HttpClient(stub), "https://server.test/"), stub);
    }

    [Fact]
    public async Task GetDay_sends_bearer_and_escaped_date_and_returns_body_verbatim()
    {
        var (client, stub) = Create(body: "{\"date\":\"2026-06-10\",\"accounts\":[]}");

        var result = await client.GetDayAsync("tok-1", "2026-06-10", CancellationToken.None);

        result.Should().Be("{\"date\":\"2026-06-10\",\"accounts\":[]}");
        stub.Requests.Should().HaveCount(1);
        stub.Requests[0].Method.Should().Be(HttpMethod.Get);
        stub.Requests[0].RequestUri!.ToString()
            .Should().Be("https://server.test/api/calendar/day?date=2026-06-10");
        stub.Requests[0].Headers.Authorization!.ToString().Should().Be("Bearer tok-1");
    }

    [Fact]
    public async Task CreateEvent_posts_raw_json_body()
    {
        var (client, stub) = Create(body: "{\"eventId\":\"evt-9\",\"replicas\":null}");

        var result = await client.CreateEventAsync("tok-1", "{\"title\":\"X\"}", CancellationToken.None);

        result.Should().Be("{\"eventId\":\"evt-9\",\"replicas\":null}");
        stub.Requests[0].Method.Should().Be(HttpMethod.Post);
        stub.Requests[0].RequestUri!.ToString().Should().Be("https://server.test/api/calendar/events");
        stub.Bodies[0].Should().Be("{\"title\":\"X\"}");
        stub.Requests[0].Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task CreateReplicas_builds_the_two_segment_event_path_escaping_both()
    {
        // Backend decision 1: an event's REST identity is {accountId}/{eventId} — two segments.
        var (client, stub) = Create(body: "{\"created\":[],\"failures\":[]}");

        await client.CreateReplicasAsync("tok-1", "acc/1", "evt 9", "{\"destinations\":[]}", CancellationToken.None);

        // AbsoluteUri (not ToString()) — ToString() cosmetically unescapes %20, hiding the wire form.
        stub.Requests[0].RequestUri!.AbsoluteUri
            .Should().Be("https://server.test/api/calendar/events/acc%2F1/evt%209/replicas");
    }

    [Fact]
    public async Task PrefixRules_list_create_update_delete_hit_expected_verbs_and_paths()
    {
        var (client, stub) = Create(body: "[]");

        await client.ListPrefixRulesAsync("tok-1", CancellationToken.None);
        await client.CreatePrefixRuleAsync("tok-1", "{\"prefix\":\"Lunch\"}", CancellationToken.None);
        await client.UpdatePrefixRuleAsync("tok-1", "rule-1", "{\"prefix\":\"Gym\"}", CancellationToken.None);
        await client.DeletePrefixRuleAsync("tok-1", "rule-1", CancellationToken.None);

        stub.Requests[0].Method.Should().Be(HttpMethod.Get);
        stub.Requests[0].RequestUri!.AbsolutePath.Should().Be("/api/calendar/prefix-rules");
        stub.Requests[1].Method.Should().Be(HttpMethod.Post);
        stub.Requests[2].Method.Should().Be(HttpMethod.Put);
        stub.Requests[2].RequestUri!.AbsolutePath.Should().Be("/api/calendar/prefix-rules/rule-1");
        stub.Requests[3].Method.Should().Be(HttpMethod.Delete);
        stub.Requests[3].RequestUri!.AbsolutePath.Should().Be("/api/calendar/prefix-rules/rule-1");
    }

    [Fact]
    public async Task Non2xx_throws_SyncClientException_with_status_code()
    {
        var (client, _) = Create(HttpStatusCode.Forbidden, "{\"error\":\"scope\"}");

        var act = () => client.GetDayAsync("tok-1", "2026-06-10", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<SyncClientException>();
        ex.Which.StatusCode.Should().Be(403);
        ex.Which.Message.Should().Contain("403");
    }

    [Fact]
    public void Ctor_throws_on_null_arguments()
    {
        var act1 = () => new HttpCalendarV2Client(null!, "https://x");
        var act2 = () => new HttpCalendarV2Client(new HttpClient(), null!);
        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task GetDay_throws_on_null_or_blank_inputs()
    {
        var (client, _) = Create();
        await ((Func<Task>)(() => client.GetDayAsync(null!, "2026-06-10", CancellationToken.None)))
            .Should().ThrowAsync<ArgumentNullException>();
        await ((Func<Task>)(() => client.GetDayAsync("tok", "  ", CancellationToken.None)))
            .Should().ThrowAsync<ArgumentNullException>();
    }
}
