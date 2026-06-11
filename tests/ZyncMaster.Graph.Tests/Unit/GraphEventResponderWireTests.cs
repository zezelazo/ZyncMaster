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

// Write-back actions (spec §6): accept / decline / tentativelyAccept with an OPTIONAL comment
// to the organizer, and cancel-as-organizer. The comment is the ONLY information that crosses
// to the origin side — and it is user-authored.
public sealed class GraphEventResponderWireTests
{
    private sealed class FakeTokens : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult("tok");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        public List<string> Urls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static GraphEventResponder Responder(CapturingHandler handler) =>
        new(new HttpClient(handler), new FakeTokens());

    [Theory]
    [InlineData(RespondAction.Accept, "accept")]
    [InlineData(RespondAction.Decline, "decline")]
    [InlineData(RespondAction.Tentative, "tentativelyAccept")]
    public async Task Respond_posts_to_the_action_verb_with_sendResponse(
        RespondAction action, string verb)
    {
        var handler = new CapturingHandler();

        await Responder(handler).RespondAsync("ev-1", action, "I will not attend");

        handler.Urls[0].Should().EndWith($"me/events/ev-1/{verb}");
        var body = JObject.Parse(handler.Bodies[0]);
        body["sendResponse"]!.Value<bool>().Should().BeTrue();
        body["comment"]!.Value<string>().Should().Be("I will not attend");
    }

    [Fact]
    public async Task Respond_omits_the_comment_key_when_no_message_was_written()
    {
        var handler = new CapturingHandler();

        await Responder(handler).RespondAsync("ev-1", RespondAction.Decline, null);

        JObject.Parse(handler.Bodies[0]).ContainsKey("comment").Should().BeFalse(
            "only a user-authored message may travel to the organizer");
    }

    [Fact]
    public async Task Cancel_posts_to_the_cancel_verb_with_the_optional_comment()
    {
        var handler = new CapturingHandler();

        await Responder(handler).CancelMeetingAsync("ev-1", "Cannot make it");

        handler.Urls[0].Should().EndWith("me/events/ev-1/cancel");
        JObject.Parse(handler.Bodies[0])["comment"]!.Value<string>().Should().Be("Cannot make it");
    }

    [Fact]
    public async Task Cancel_sends_an_empty_object_when_no_comment()
    {
        var handler = new CapturingHandler();

        await Responder(handler).CancelMeetingAsync("ev-1", null);

        JObject.Parse(handler.Bodies[0]).Properties().Should().BeEmpty();
    }
}
