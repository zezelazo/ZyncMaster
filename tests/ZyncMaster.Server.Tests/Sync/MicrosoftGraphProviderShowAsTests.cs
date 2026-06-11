using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// Calendar v2: the replica engine copies ONLY start/end/showAs (+ the manual mask title), so
// the Graph read must surface the event's free/busy status on AppointmentRecord.ShowAs.
public class MicrosoftGraphProviderShowAsTests
{
    private sealed class FakeTokens : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult("tok");
    }

    private sealed class OnePageHandler : HttpMessageHandler
    {
        private readonly string _json;
        public string? LastUrl { get; private set; }
        public OnePageHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private const string Page = """
        {
          "value": [
            {
              "id": "ev-1",
              "iCalUId": "uid-1",
              "subject": "Standup",
              "isAllDay": false,
              "isCancelled": false,
              "showAs": "oof",
              "start": { "dateTime": "2026-06-15T10:00:00.0000000", "timeZone": "UTC" },
              "end":   { "dateTime": "2026-06-15T10:30:00.0000000", "timeZone": "UTC" }
            }
          ]
        }
        """;

    [Fact]
    public async Task ReadWindow_maps_showAs_and_requests_it_in_the_select()
    {
        var handler = new OnePageHandler(Page);
        var provider = new MicrosoftGraphProvider(
            new HttpClient(handler), new FakeTokens(), Mock.Of<ICalendarTarget>());

        var records = await provider.ReadWindowAsync(
            "CAL",
            new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.Zero));

        records.Should().ContainSingle();
        records[0].ShowAs.Should().Be("oof");
        handler.LastUrl.Should().Contain("showAs", "the $select must request the field");
    }

    [Fact]
    public async Task ReadWindow_defaults_showAs_to_empty_when_graph_omits_it()
    {
        const string noShowAs = """
            {
              "value": [
                {
                  "id": "ev-1",
                  "subject": "Standup",
                  "start": { "dateTime": "2026-06-15T10:00:00.0000000", "timeZone": "UTC" },
                  "end":   { "dateTime": "2026-06-15T10:30:00.0000000", "timeZone": "UTC" }
                }
              ]
            }
            """;
        var provider = new MicrosoftGraphProvider(
            new HttpClient(new OnePageHandler(noShowAs)), new FakeTokens(), Mock.Of<ICalendarTarget>());

        var records = await provider.ReadWindowAsync(
            "CAL",
            new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.Zero));

        records[0].ShowAs.Should().Be("", "consumers degrade an empty ShowAs to 'busy'");
    }
}
