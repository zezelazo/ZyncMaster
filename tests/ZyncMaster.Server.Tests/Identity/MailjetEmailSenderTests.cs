using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Options;
using ZyncMaster.Server.Infrastructure.Email;
using Xunit;

namespace ZyncMaster.Server.Tests.Identity;

// Unit tests for the Mailjet REST v3.1 sender. The live HTTP call to Mailjet is infra and is
// NOT tested against the network; instead the JSON payload construction, the Basic auth header
// and the non-success error handling are exercised through a captured HttpMessageHandler — the
// same pattern MicrosoftTokenServiceTests uses for the token endpoint.
public class MailjetEmailSenderTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public CapturingHandler(HttpResponseMessage response) => _response = response;

        public string? CapturedBody { get; private set; }
        public Uri? CapturedUri { get; private set; }
        public AuthenticationHeaderValue? CapturedAuth { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedUri = request.RequestUri;
            CapturedAuth = request.Headers.Authorization;
            CapturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _response;
        }
    }

    private static IOptions<ServerOptions> Options(
        string apiKey = "MJ-KEY",
        string apiSecret = "MJ-SECRET",
        string fromEmail = "noreply@zyncmaster.test",
        string fromName = "Zync Master") =>
        Microsoft.Extensions.Options.Options.Create(new ServerOptions
        {
            MailjetApiKey = apiKey,
            MailjetApiSecret = apiSecret,
            MailjetFromEmail = fromEmail,
            MailjetFromName = fromName,
        });

    private static MailjetEmailSender BuildSender(CapturingHandler handler, IOptions<ServerOptions>? opts = null) =>
        new(new HttpClient(handler), opts ?? Options());

    private static HttpResponseMessage Ok() =>
        new(HttpStatusCode.OK) { Content = new StringContent("{\"Messages\":[{\"Status\":\"success\"}]}") };

    [Fact]
    public async Task SendAsync_posts_v31_payload_to_the_send_endpoint()
    {
        var handler = new CapturingHandler(Ok());
        var sender = BuildSender(handler);

        await sender.SendAsync("user@example.com", "Sign in to Zync Master", "<a href=\"https://app/cb?token=SECRET\">Link</a>");

        handler.CapturedUri!.ToString().Should().Be("https://api.mailjet.com/v3.1/send");

        using var doc = JsonDocument.Parse(handler.CapturedBody!);
        var message = doc.RootElement.GetProperty("Messages")[0];

        message.GetProperty("From").GetProperty("Email").GetString().Should().Be("noreply@zyncmaster.test");
        message.GetProperty("From").GetProperty("Name").GetString().Should().Be("Zync Master");
        message.GetProperty("To")[0].GetProperty("Email").GetString().Should().Be("user@example.com");
        message.GetProperty("Subject").GetString().Should().Be("Sign in to Zync Master");
        message.GetProperty("HTMLPart").GetString().Should()
            .Be("<a href=\"https://app/cb?token=SECRET\">Link</a>");
    }

    [Fact]
    public async Task SendAsync_sets_basic_auth_header_from_key_and_secret()
    {
        var handler = new CapturingHandler(Ok());
        var sender = BuildSender(handler, Options(apiKey: "the-key", apiSecret: "the-secret"));

        await sender.SendAsync("user@example.com", "S", "<p>b</p>");

        handler.CapturedAuth.Should().NotBeNull();
        handler.CapturedAuth!.Scheme.Should().Be("Basic");
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("the-key:the-secret"));
        handler.CapturedAuth.Parameter.Should().Be(expected);
    }

    [Fact]
    public async Task SendAsync_non_success_throws_with_status_but_not_the_body()
    {
        // A non-2xx Mailjet response must surface the status for diagnostics but MUST NOT echo
        // the response body into the exception — the request body carried the magic-link token
        // and Mailjet error payloads can reflect request fields back.
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"ErrorMessage\":\"token=LEAKED_SECRET reflected\"}"),
        });
        var sender = BuildSender(handler);

        var act = async () => await sender.SendAsync("user@example.com", "S", "<a>token=LEAKED_SECRET</a>");

        var ex = await act.Should().ThrowAsync<EmailDeliveryException>();
        ex.Which.Message.Should().Contain("401");
        ex.Which.Message.Should().NotContain("LEAKED_SECRET");
    }

    [Fact]
    public async Task SendAsync_does_not_include_html_body_in_exception_message()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("upstream error"),
        });
        var sender = BuildSender(handler);
        const string htmlBody = "<a href=\"https://app/cb?token=MAGIC_TOKEN_VALUE\">Sign in</a>";

        var act = async () => await sender.SendAsync("user@example.com", "S", htmlBody);

        var ex = await act.Should().ThrowAsync<EmailDeliveryException>();
        ex.Which.Message.Should().NotContain("MAGIC_TOKEN_VALUE");
        ex.Which.Message.Should().Contain("500");
    }

    [Fact]
    public void Ctor_rejects_null_dependencies()
    {
        var act1 = () => new MailjetEmailSender(null!, Options());
        act1.Should().Throw<ArgumentNullException>();

        var act2 = () => new MailjetEmailSender(new HttpClient(new CapturingHandler(Ok())), null!);
        act2.Should().Throw<ArgumentNullException>();
    }
}
