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
}
