using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests;

// Device self-management methods on HttpPairsClient: rename + getDeviceMe. Both carry the device
// api key in X-Api-Key; the server resolves the deviceId from that key, so the client never sends
// an id. Mirrors the StubHandler pattern used by HttpPairsClientTests.
public sealed class HttpPairsClientDeviceTests
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
            if (request.Headers.Authorization is { } auth)
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

    private static (HttpPairsClient client, StubHandler stub) Make(HttpStatusCode status, string body)
    {
        var stub = new StubHandler(status, body);
        var http = new HttpClient(stub);
        var client = new HttpPairsClient(http, "https://srv.example.com");
        return (client, stub);
    }

    [Fact]
    public async Task RenameDevice_PostsNameWithKeyAndParsesResult()
    {
        var (client, stub) = Make(HttpStatusCode.OK,
            @"{ ""deviceId"": ""dev-1"", ""name"": ""Office Laptop"" }");

        var result = await client.RenameDeviceAsync(Key, "Office Laptop", CancellationToken.None);

        stub.LastRequest!.Method.Should().Be(HttpMethod.Post);
        stub.LastRequest!.RequestUri!.ToString().Should().Be("https://srv.example.com/api/devices/rename");
        stub.LastApiKey.Should().Be(Key);

        var body = JObject.Parse(stub.LastBody!);
        body["name"]!.Value<string>().Should().Be("Office Laptop");
        // The client must NOT smuggle a deviceId — the server reads it from the api key.
        body.ContainsKey("deviceId").Should().BeFalse();

        result.DeviceId.Should().Be("dev-1");
        result.Name.Should().Be("Office Laptop");
    }

    [Fact]
    public async Task GetDeviceMe_GetsWithKeyAndParsesResult()
    {
        var (client, stub) = Make(HttpStatusCode.OK,
            @"{ ""deviceId"": ""dev-2"", ""name"": ""My Workstation"", ""platform"": ""windows"" }");

        var result = await client.GetDeviceMeAsync(Key, CancellationToken.None);

        stub.LastRequest!.Method.Should().Be(HttpMethod.Get);
        stub.LastRequest!.RequestUri!.ToString().Should().Be("https://srv.example.com/api/devices/me");
        stub.LastApiKey.Should().Be(Key);

        result.DeviceId.Should().Be("dev-2");
        result.Name.Should().Be("My Workstation");
        result.Platform.Should().Be("windows");
    }

    [Fact]
    public async Task Heartbeat_PostsEmptyBodyWithKeyAndParsesLeaseUntil()
    {
        var (client, stub) = Make(HttpStatusCode.OK,
            @"{ ""leaseUntil"": ""2026-06-05T12:00:00+00:00"" }");

        var leaseUntil = await client.HeartbeatAsync(Key, CancellationToken.None);

        stub.LastRequest!.Method.Should().Be(HttpMethod.Post);
        stub.LastRequest!.RequestUri!.ToString().Should().Be("https://srv.example.com/api/devices/heartbeat");
        stub.LastApiKey.Should().Be(Key);
        // The deviceId is resolved from the api key; the body must carry no id.
        var body = JObject.Parse(stub.LastBody!);
        body.ContainsKey("deviceId").Should().BeFalse();

        leaseUntil.Should().Be(DateTimeOffset.Parse("2026-06-05T12:00:00+00:00",
            System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Heartbeat_NullKey_Throws()
    {
        var (client, _) = Make(HttpStatusCode.OK, "{}");
        Func<Task> act = () => client.HeartbeatAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Heartbeat_ServerError_Throws()
    {
        var (client, _) = Make(HttpStatusCode.Unauthorized, @"{ ""error"": ""nope"" }");
        Func<Task> act = () => client.HeartbeatAsync(Key, CancellationToken.None);
        (await act.Should().ThrowAsync<SyncClientException>())
            .Which.Message.Should().Contain("401");
    }

    [Fact]
    public async Task RenameDevice_ServerError_Throws()
    {
        var (client, _) = Make(HttpStatusCode.BadRequest, @"{ ""error"": ""bad name"" }");

        Func<Task> act = () => client.RenameDeviceAsync(Key, "", CancellationToken.None);

        (await act.Should().ThrowAsync<SyncClientException>())
            .Which.Message.Should().Contain("400");
    }

    [Fact]
    public async Task RenameDevice_NullKey_Throws()
    {
        var (client, _) = Make(HttpStatusCode.OK, "{}");
        Func<Task> act = () => client.RenameDeviceAsync(null!, "x", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CheckDeviceNameAvailable_GetsWithKeyAndQueryAndParsesAvailableTrue()
    {
        var (client, stub) = Make(HttpStatusCode.OK, @"{ ""available"": true }");

        var available = await client.CheckDeviceNameAvailableAsync(Key, "Office Laptop", CancellationToken.None);

        stub.LastRequest!.Method.Should().Be(HttpMethod.Get);
        // The name is percent-escaped on the wire; RequestUri.ToString() shows the decoded form.
        stub.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/devices/name-available");
        stub.LastRequest!.RequestUri!.Query.Should().Be("?name=Office%20Laptop");
        stub.LastApiKey.Should().Be(Key);
        available.Should().BeTrue();
    }

    [Fact]
    public async Task CheckDeviceNameAvailable_ParsesAvailableFalse()
    {
        var (client, _) = Make(HttpStatusCode.OK, @"{ ""available"": false }");

        (await client.CheckDeviceNameAvailableAsync(Key, "Taken", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task CheckDeviceNameAvailable_InvalidReasonReportsFalse()
    {
        var (client, _) = Make(HttpStatusCode.OK, @"{ ""available"": false, ""reason"": ""invalid"" }");

        (await client.CheckDeviceNameAvailableAsync(Key, "   ", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task CheckDeviceNameAvailable_NullKey_Throws()
    {
        var (client, _) = Make(HttpStatusCode.OK, "{}");
        Func<Task> act = () => client.CheckDeviceNameAvailableAsync(null!, "x", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ---------------- RegisterDeviceAsync ----------------

    [Fact]
    public async Task RegisterDevice_PostsNameWithBearerAndParsesResult()
    {
        var (client, stub) = Make(HttpStatusCode.OK,
            @"{ ""deviceId"": ""dev-99"", ""apiKey"": ""kid.secret"", ""leaseUntil"": ""2026-06-06T12:00:00+00:00"" }");

        var result = await client.RegisterDeviceAsync("bearer-1", "My Laptop", CancellationToken.None);

        stub.LastRequest!.Method.Should().Be(HttpMethod.Post);
        stub.LastRequest!.RequestUri!.ToString().Should().Be("https://srv.example.com/api/devices/register");
        // Registration is the HUMAN-only surface: it carries the IDENTITY BEARER, never an api key.
        stub.LastBearer.Should().Be("bearer-1");
        stub.LastApiKey.Should().BeNull();

        var body = JObject.Parse(stub.LastBody!);
        body["name"]!.Value<string>().Should().Be("My Laptop");
        // The server reads the owning user from the token; the body must NOT smuggle a userId.
        body.ContainsKey("userId").Should().BeFalse();

        result.DeviceId.Should().Be("dev-99");
        result.ApiKey.Should().Be("kid.secret");
        result.LeaseUntil.Should().Be(DateTimeOffset.Parse("2026-06-06T12:00:00+00:00",
            System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task RegisterDevice_ParsesNullLeaseWhenAbsent()
    {
        var (client, _) = Make(HttpStatusCode.OK,
            @"{ ""deviceId"": ""dev-1"", ""apiKey"": ""kid.secret"" }");

        var result = await client.RegisterDeviceAsync("bearer-1", "Laptop", CancellationToken.None);

        result.LeaseUntil.Should().BeNull();
    }

    [Fact]
    public async Task RegisterDevice_ServerError_Throws()
    {
        var (client, _) = Make(HttpStatusCode.Unauthorized, @"{ ""error"": ""no identity"" }");

        Func<Task> act = () => client.RegisterDeviceAsync("bearer-1", "Laptop", CancellationToken.None);

        (await act.Should().ThrowAsync<SyncClientException>())
            .Which.Message.Should().Contain("401");
    }

    [Fact]
    public async Task RegisterDevice_NullBearer_Throws()
    {
        var (client, _) = Make(HttpStatusCode.OK, "{}");
        Func<Task> act = () => client.RegisterDeviceAsync(null!, "Laptop", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RegisterDevice_NullName_Throws()
    {
        var (client, _) = Make(HttpStatusCode.OK, "{}");
        Func<Task> act = () => client.RegisterDeviceAsync("bearer-1", null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
