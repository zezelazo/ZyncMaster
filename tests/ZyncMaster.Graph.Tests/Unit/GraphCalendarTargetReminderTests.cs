using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.Graph.Tests;

// FIX 5 — the reminder (isReminderOn / reminderMinutesBeforeStart) must be written ONLY on Create.
// On Update it must be OMITTED so a reminder the user changed on the destination event is not
// silently reset to the default on every sync. These tests capture the exact JSON body the target
// sends to Graph for a create vs an update and assert the reminder presence.
public sealed class GraphCalendarTargetReminderTests
{
    private static readonly Guid PropGuid = new Guid("11111111-2222-3333-4444-555555555555");

    private sealed class FakeTokenProvider : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult("tok");
    }

    // Captures the request body of each call; returns 200 with a minimal event JSON so Create can
    // read back an id.
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        public List<HttpMethod> Methods { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"ev-1\"}", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static EventDraft Draft() => new()
    {
        Subject = "Standup",
        BodyHtml = "<p>x</p>",
        Start = new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero),
        End = new DateTimeOffset(2026, 5, 1, 9, 30, 0, TimeSpan.Zero),
        TimeZoneId = "UTC",
        ReminderMinutesBeforeStart = 30,
        ExternalId = "src-1",
    };

    [Fact]
    public async Task Create_writes_the_reminder()
    {
        var handler = new CapturingHandler();
        var sut = new GraphCalendarTarget(new HttpClient(handler), new FakeTokenProvider(), PropGuid);

        await sut.CreateEventAsync("CAL", Draft());

        var body = JObject.Parse(handler.Bodies[0]);
        body["isReminderOn"]!.Value<bool>().Should().BeTrue();
        body["reminderMinutesBeforeStart"]!.Value<int>().Should().Be(30);
    }

    [Fact]
    public async Task Update_does_not_write_the_reminder_so_user_edits_are_preserved()
    {
        var handler = new CapturingHandler();
        var sut = new GraphCalendarTarget(new HttpClient(handler), new FakeTokenProvider(), PropGuid);

        await sut.UpdateEventAsync("ev-1", Draft());

        var body = JObject.Parse(handler.Bodies[0]);
        body.ContainsKey("isReminderOn").Should().BeFalse(
            "update must not overwrite a reminder the user customised on the destination");
        body.ContainsKey("reminderMinutesBeforeStart").Should().BeFalse();

        // The rest of the event is still patched (subject/body/start/end) so the sync still applies.
        body["subject"]!.Value<string>().Should().Be("Standup");
    }
}
