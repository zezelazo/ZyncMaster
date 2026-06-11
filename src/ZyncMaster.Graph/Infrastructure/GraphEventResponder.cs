using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ZyncMaster.Graph;

public sealed class GraphEventResponder : IEventResponder
{
    private readonly GraphJsonHttp _io;

    public GraphEventResponder(HttpClient http, IGraphTokenProvider auth)
        => _io = new GraphJsonHttp(http, auth);

    public async Task RespondAsync(
        string eventId, RespondAction action, string? comment, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("eventId required.", nameof(eventId));

        var verb = action switch
        {
            RespondAction.Accept => "accept",
            RespondAction.Decline => "decline",
            RespondAction.Tentative => "tentativelyAccept",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        // sendResponse:true — the whole point is telling the organizer. The comment key is
        // OMITTED (not sent empty) when the user wrote nothing: only user-authored text may
        // cross to the origin side (spec §6/§12, the single explicit exception).
        var body = new JObject { ["sendResponse"] = true };
        if (!string.IsNullOrWhiteSpace(comment))
            body["comment"] = comment;

        await _io.SendJsonAsync(HttpMethod.Post,
            $"me/events/{Uri.EscapeDataString(eventId)}/{verb}",
            body.ToString(Formatting.None), ct).ConfigureAwait(false);
    }

    public async Task CancelMeetingAsync(string eventId, string? comment, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("eventId required.", nameof(eventId));

        var body = new JObject();
        if (!string.IsNullOrWhiteSpace(comment))
            body["comment"] = comment;

        await _io.SendJsonAsync(HttpMethod.Post,
            $"me/events/{Uri.EscapeDataString(eventId)}/cancel",
            body.ToString(Formatting.None), ct).ConfigureAwait(false);
    }
}
