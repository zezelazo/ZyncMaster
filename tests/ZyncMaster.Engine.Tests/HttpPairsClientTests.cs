using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using ZyncMaster.Core;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests;

public sealed class HttpPairsClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _responseBody;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public string? LastApiKey { get; private set; }
        public string? LastBearer { get; private set; }

        public StubHandler(HttpStatusCode status, string responseBody)
        {
            _status = status;
            _responseBody = responseBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Headers.TryGetValues("X-Api-Key", out var values))
                foreach (var v in values) LastApiKey = v;
            if (request.Headers.Authorization is { Scheme: "Bearer" } auth)
                LastBearer = auth.Parameter;
            if (request.Content != null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private const string Key = "the-api-key";
    private const string Bearerr = "the-identity-bearer";

    private static (HttpPairsClient client, StubHandler stub) Make(HttpStatusCode status, string body)
    {
        var stub = new StubHandler(status, body);
        var http = new HttpClient(stub);
        var client = new HttpPairsClient(http, "https://srv.example.com");
        return (client, stub);
    }

    private static AppointmentRecord SampleEvent() => new AppointmentRecord
    {
        Id = "evt-1",
        Subject = "Standup",
        StartOffset = new DateTimeOffset(2025, 5, 10, 9, 0, 0, TimeSpan.Zero),
        EndOffset = new DateTimeOffset(2025, 5, 10, 9, 30, 0, TimeSpan.Zero),
        Duration = 30,
    };

    [Fact]
    public void Ctor_NullHttp_Throws()
    {
        Action act = () => new HttpPairsClient(null!, "https://x");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullBaseUrl_Throws()
    {
        Action act = () => new HttpPairsClient(new HttpClient(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ListAccounts_GetsAndParsesAndSendsBearer()
    {
        var (client, stub) = Make(HttpStatusCode.OK,
            @"[ { ""accountRef"": ""a1"", ""displayName"": ""Work"", ""isDefault"": true },
                { ""accountRef"": ""a2"", ""displayName"": ""Home"", ""isDefault"": false } ]");

        var result = await client.ListAccountsAsync(Bearerr, CancellationToken.None);

        stub.LastRequest!.Method.Should().Be(HttpMethod.Get);
        stub.LastRequest!.RequestUri!.ToString().Should().Be("https://srv.example.com/api/accounts");
        // Human-only management surface: identity bearer, NOT the device api key.
        stub.LastBearer.Should().Be(Bearerr);
        stub.LastApiKey.Should().BeNull();
        result.Should().HaveCount(2);
        result[0].AccountRef.Should().Be("a1");
        result[0].DisplayName.Should().Be("Work");
        result[0].IsDefault.Should().BeTrue();
        result[1].IsDefault.Should().BeFalse();
    }

    [Fact]
    public async Task ListCalendars_GetsAccountScopedUrlAndParses()
    {
        var (client, stub) = Make(HttpStatusCode.OK,
            @"[ { ""id"": ""c1"", ""displayName"": ""Calendar"", ""isDefault"": true, ""owner"": ""me@x"" } ]");

        var result = await client.ListCalendarsAsync(Bearerr, "a1", CancellationToken.None);

        stub.LastRequest!.Method.Should().Be(HttpMethod.Get);
        stub.LastRequest!.RequestUri!.ToString().Should().Be("https://srv.example.com/api/accounts/a1/calendars");
        stub.LastBearer.Should().Be(Bearerr);
        stub.LastApiKey.Should().BeNull();
        result.Should().ContainSingle();
        result[0].Id.Should().Be("c1");
        result[0].DisplayName.Should().Be("Calendar");
        result[0].IsDefault.Should().BeTrue();
        result[0].Owner.Should().Be("me@x");
    }

    [Fact]
    public async Task CreateCalendar_PostsNameToAccountScopedUrlAndParses()
    {
        var (client, stub) = Make(HttpStatusCode.OK,
            @"{ ""id"": ""new-1"", ""displayName"": ""Travel"", ""isDefault"": false, ""owner"": ""me@x"" }");

        var result = await client.CreateCalendarAsync(Bearerr, "a1", "Travel", CancellationToken.None);

        stub.LastRequest!.Method.Should().Be(HttpMethod.Post);
        stub.LastRequest!.RequestUri!.ToString().Should().Be("https://srv.example.com/api/accounts/a1/calendars");
        // Human-only management surface: identity bearer, NOT the device api key.
        stub.LastBearer.Should().Be(Bearerr);
        stub.LastApiKey.Should().BeNull();
        JObject.Parse(stub.LastBody!)["name"]!.Value<string>().Should().Be("Travel");

        result.Id.Should().Be("new-1");
        result.DisplayName.Should().Be("Travel");
        result.IsDefault.Should().BeFalse();
        result.Owner.Should().Be("me@x");
    }

    [Fact]
    public async Task CreateCalendar_NullName_Throws()
    {
        var (client, _) = Make(HttpStatusCode.OK, "{}");

        Func<Task> act = () => client.CreateCalendarAsync(Bearerr, "a1", null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CreatePair_PostsBodyAndParsesResult()
    {
        var (client, stub) = Make(HttpStatusCode.OK,
            @"{ ""id"": ""p1"", ""name"": ""Mirror"", ""intervalMin"": 15, ""state"": ""active"",
                ""source"": { ""provider"": ""OutlookCom"", ""calendarId"": ""s"", ""calendarName"": ""Src"" },
                ""destination"": { ""provider"": ""MicrosoftGraph"", ""accountRef"": ""a2"", ""calendarId"": ""d"", ""calendarName"": ""Dst"" } }");

        var source = new Endpoint { Provider = "OutlookCom", CalendarId = "s", CalendarName = "Src" };
        var dest = new Endpoint { Provider = "MicrosoftGraph", AccountRef = "a2", CalendarId = "d", CalendarName = "Dst" };

        var result = await client.CreatePairAsync(Bearerr, "Mirror", source, dest, 15, CancellationToken.None);

        stub.LastRequest!.Method.Should().Be(HttpMethod.Post);
        stub.LastRequest!.RequestUri!.ToString().Should().Be("https://srv.example.com/api/pairs");
        stub.LastBearer.Should().Be(Bearerr);
        stub.LastApiKey.Should().BeNull();

        var body = JObject.Parse(stub.LastBody!);
        body["name"]!.Value<string>().Should().Be("Mirror");
        body["intervalMin"]!.Value<int>().Should().Be(15);
        body["source"]!["provider"]!.Value<string>().Should().Be("OutlookCom");
        body["destination"]!["accountRef"]!.Value<string>().Should().Be("a2");

        result.Id.Should().Be("p1");
        result.IntervalMin.Should().Be(15);
        result.State.Should().Be("active");
        result.Source.Provider.Should().Be("OutlookCom");
        result.Destination.AccountRef.Should().Be("a2");
    }

    [Fact]
    public async Task ListPairs_GetsAndParsesListWithNestedResult()
    {
        var (client, stub) = Make(HttpStatusCode.OK,
            @"[ { ""id"": ""p1"", ""name"": ""M"", ""intervalMin"": 10, ""state"": ""active"",
                 ""source"": { ""provider"": ""OutlookCom"", ""calendarId"": ""s"", ""calendarName"": ""S"" },
                 ""destination"": { ""provider"": ""MicrosoftGraph"", ""accountRef"": ""a"", ""calendarId"": ""d"", ""calendarName"": ""D"" },
                 ""lastRunUtc"": ""2025-05-10T09:00:00+00:00"",
                 ""lastResult"": { ""created"": 1, ""updated"": 2, ""deleted"": 0, ""skipped"": 4, ""failures"": [""x""] } } ]");

        var result = await client.ListPairsAsync(Bearerr, CancellationToken.None);

        stub.LastRequest!.Method.Should().Be(HttpMethod.Get);
        stub.LastRequest!.RequestUri!.ToString().Should().Be("https://srv.example.com/api/pairs");
        stub.LastBearer.Should().Be(Bearerr);
        stub.LastApiKey.Should().BeNull();
        result.Should().ContainSingle();
        result[0].Id.Should().Be("p1");
        result[0].IntervalMin.Should().Be(10);
        result[0].LastRunUtc.Should().Be(new DateTimeOffset(2025, 5, 10, 9, 0, 0, TimeSpan.Zero));
        result[0].LastResult!.Created.Should().Be(1);
        result[0].LastResult!.Skipped.Should().Be(4);
        result[0].LastResult!.Failures.Should().ContainSingle().Which.Should().Be("x");
    }

    [Fact]
    public async Task UpdatePair_PatchesOnlyProvidedFields()
    {
        var (client, stub) = Make(HttpStatusCode.OK,
            @"{ ""id"": ""p1"", ""name"": ""M"", ""intervalMin"": 20, ""state"": ""paused"",
                ""source"": { ""provider"": ""OutlookCom"", ""calendarId"": ""s"", ""calendarName"": ""S"" },
                ""destination"": { ""provider"": ""MicrosoftGraph"", ""calendarId"": ""d"", ""calendarName"": ""D"" } }");

        var result = await client.UpdatePairAsync(Bearerr, "p1", name: null, intervalMin: 20, state: "paused", CancellationToken.None);

        stub.LastRequest!.Method.Should().Be(HttpMethod.Patch);
        stub.LastRequest!.RequestUri!.ToString().Should().Be("https://srv.example.com/api/pairs/p1");
        stub.LastBearer.Should().Be(Bearerr);
        stub.LastApiKey.Should().BeNull();

        var body = JObject.Parse(stub.LastBody!);
        body.ContainsKey("name").Should().BeFalse();
        body["intervalMin"]!.Value<int>().Should().Be(20);
        body["state"]!.Value<string>().Should().Be("paused");

        result.State.Should().Be("paused");
        result.IntervalMin.Should().Be(20);
    }

    [Fact]
    public async Task DeletePair_SendsDeleteToPairUrl()
    {
        var (client, stub) = Make(HttpStatusCode.NoContent, "");

        await client.DeletePairAsync(Bearerr, "p1", CancellationToken.None);

        stub.LastRequest!.Method.Should().Be(HttpMethod.Delete);
        stub.LastRequest!.RequestUri!.ToString().Should().Be("https://srv.example.com/api/pairs/p1");
        stub.LastBearer.Should().Be(Bearerr);
        stub.LastApiKey.Should().BeNull();
    }

    [Fact]
    public async Task PushPair_PostsEventsAndParsesMirrorResult()
    {
        var (client, stub) = Make(HttpStatusCode.OK,
            @"{ ""created"": 2, ""updated"": 1, ""deleted"": 0, ""skipped"": 3, ""failures"": [""boom""] }");

        var result = await client.PushPairAsync(Key, "p1", new[] { SampleEvent() }, CancellationToken.None);

        stub.LastRequest!.Method.Should().Be(HttpMethod.Post);
        stub.LastRequest!.RequestUri!.ToString().Should().Be("https://srv.example.com/api/pairs/p1/push");
        stub.LastApiKey.Should().Be(Key);

        var body = JObject.Parse(stub.LastBody!);
        var events = (JArray)body["events"]!;
        events.Should().HaveCount(1);
        events[0]!["id"]!.Value<string>().Should().Be("evt-1");

        result.Created.Should().Be(2);
        result.Updated.Should().Be(1);
        result.Skipped.Should().Be(3);
        result.Failures.Should().ContainSingle().Which.Should().Be("boom");
    }

    [Fact]
    public async Task RunPair_PostsToRunUrlAndParsesMirrorResult()
    {
        var (client, stub) = Make(HttpStatusCode.OK,
            @"{ ""created"": 5, ""updated"": 0, ""deleted"": 1, ""skipped"": 0, ""failures"": [] }");

        var result = await client.RunPairAsync(Key, "p1", CancellationToken.None);

        stub.LastRequest!.Method.Should().Be(HttpMethod.Post);
        stub.LastRequest!.RequestUri!.ToString().Should().Be("https://srv.example.com/api/pairs/p1/run");
        stub.LastApiKey.Should().Be(Key);
        result.Created.Should().Be(5);
        result.Deleted.Should().Be(1);
        result.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task UnlinkAccount_DeletesAndParsesAffectedPairIds()
    {
        var (client, stub) = Make(HttpStatusCode.OK,
            @"{ ""affectedPairIds"": [ ""p1"", ""p2"" ] }");

        var result = await client.UnlinkAccountAsync(Bearerr, "a1", CancellationToken.None);

        stub.LastRequest!.Method.Should().Be(HttpMethod.Delete);
        stub.LastRequest!.RequestUri!.ToString().Should().Be("https://srv.example.com/api/accounts/a1");
        stub.LastBearer.Should().Be(Bearerr);
        stub.LastApiKey.Should().BeNull();
        result.Should().BeEquivalentTo(new[] { "p1", "p2" });
    }

    [Fact]
    public async Task ServerError_Throws()
    {
        var (client, _) = Make(HttpStatusCode.InternalServerError, @"{ ""error"": ""kaboom"" }");

        Func<Task> act = () => client.ListPairsAsync(Key, CancellationToken.None);

        (await act.Should().ThrowAsync<SyncClientException>())
            .Which.Message.Should().Contain("500");
    }
}
