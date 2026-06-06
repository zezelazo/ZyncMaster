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

public sealed class HttpClientsTests
{
    // Captures the outgoing request and returns a canned response.
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _responseBody;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public StubHandler(HttpStatusCode status, string responseBody)
        {
            _status = status;
            _responseBody = responseBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json"),
            };
        }
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
    public async Task PairingClient_StartAsync_PostsNameAndParsesResult()
    {
        var stub = new StubHandler(HttpStatusCode.OK, @"{ ""pairingId"": ""p-123"", ""code"": ""ABCD"" }");
        var http = new HttpClient(stub);
        var client = new HttpPairingClient(http, "https://srv.example.com");

        var result = await client.StartAsync("My Laptop", CancellationToken.None);

        stub.LastRequest!.Method.Should().Be(HttpMethod.Post);
        stub.LastRequest!.RequestUri!.ToString().Should().Be("https://srv.example.com/api/pair/start");
        JObject.Parse(stub.LastBody!)["name"]!.Value<string>().Should().Be("My Laptop");

        result.PairingId.Should().Be("p-123");
        result.Code.Should().Be("ABCD");
    }

    [Fact]
    public async Task PairingClient_CompleteAsync_PostsPairingIdAndParsesResult()
    {
        var stub = new StubHandler(HttpStatusCode.OK,
            @"{ ""approved"": true, ""apiKey"": ""key-xyz"", ""deviceId"": ""dev-9"" }");
        var http = new HttpClient(stub);
        var client = new HttpPairingClient(http, "https://srv.example.com/");

        var result = await client.CompleteAsync("p-123", "verifier-abc", CancellationToken.None);

        stub.LastRequest!.Method.Should().Be(HttpMethod.Post);
        stub.LastRequest!.RequestUri!.ToString().Should().Be("https://srv.example.com/api/pair/complete");
        JObject.Parse(stub.LastBody!)["pairingId"]!.Value<string>().Should().Be("p-123");
        JObject.Parse(stub.LastBody!)["verifier"]!.Value<string>().Should().Be("verifier-abc");

        result.Approved.Should().BeTrue();
        result.ApiKey.Should().Be("key-xyz");
        result.DeviceId.Should().Be("dev-9");
    }

    [Fact]
    public async Task PairingClient_CompleteAsync_NotYetApproved_ParsesFalse()
    {
        var stub = new StubHandler(HttpStatusCode.OK, @"{ ""approved"": false }");
        var http = new HttpClient(stub);
        var client = new HttpPairingClient(http, "https://srv.example.com");

        var result = await client.CompleteAsync("p-123", "verifier-abc", CancellationToken.None);

        result.Approved.Should().BeFalse();
        result.ApiKey.Should().BeNull();
    }

    [Fact]
    public async Task SyncClient_PushAsync_Happy_SendsApiKeyHeaderAndEventsBody()
    {
        var stub = new StubHandler(HttpStatusCode.OK,
            @"{ ""created"": 2, ""updated"": 1, ""deleted"": 0, ""skipped"": 3, ""failures"": [""boom""] }");
        var http = new HttpClient(stub);
        var client = new HttpSyncClient(http, "https://srv.example.com");

        var result = await client.PushAsync("the-api-key", new[] { SampleEvent() }, CancellationToken.None);

        stub.LastRequest!.Method.Should().Be(HttpMethod.Post);
        stub.LastRequest!.RequestUri!.ToString().Should().Be("https://srv.example.com/api/sync/calendar");
        stub.LastRequest!.Headers.GetValues("X-Api-Key").Should().ContainSingle().Which.Should().Be("the-api-key");

        var body = JObject.Parse(stub.LastBody!);
        var events = (JArray)body["events"]!;
        events.Should().HaveCount(1);
        events[0]!["id"]!.Value<string>().Should().Be("evt-1");

        result.Created.Should().Be(2);
        result.Updated.Should().Be(1);
        result.Deleted.Should().Be(0);
        result.Skipped.Should().Be(3);
        result.Failures.Should().ContainSingle().Which.Should().Be("boom");
        result.NoConnectedAccount.Should().BeFalse();
    }

    [Fact]
    public async Task SyncClient_PushAsync_Conflict_ReturnsNoConnectedAccount()
    {
        var stub = new StubHandler(HttpStatusCode.Conflict,
            @"{ ""error"": ""no_connected_account"", ""message"": ""Connect first."" }");
        var http = new HttpClient(stub);
        var client = new HttpSyncClient(http, "https://srv.example.com");

        var result = await client.PushAsync("k", new[] { SampleEvent() }, CancellationToken.None);

        result.NoConnectedAccount.Should().BeTrue();
        result.Created.Should().Be(0);
    }

    [Fact]
    public async Task SyncClient_PushAsync_ServerError_Throws()
    {
        var stub = new StubHandler(HttpStatusCode.InternalServerError, @"{ ""error"": ""kaboom"" }");
        var http = new HttpClient(stub);
        var client = new HttpSyncClient(http, "https://srv.example.com");

        Func<Task> act = () => client.PushAsync("k", new[] { SampleEvent() }, CancellationToken.None);

        (await act.Should().ThrowAsync<SyncClientException>())
            .Which.Message.Should().Contain("500");
    }

    [Fact]
    public void PairingClient_Ctor_NullHttp_Throws()
    {
        Action act = () => new HttpPairingClient(null!, "https://x");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SyncClient_Ctor_NullBaseUrl_Throws()
    {
        Action act = () => new HttpSyncClient(new HttpClient(), null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
