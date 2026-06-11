using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Graph;

public enum RespondAction
{
    Accept,
    Decline,   // "will not attend"
    Tentative,
}

// Write-back actions against an ORIGIN event (spec §6). Cancel here is the organizer-meeting
// path (POST /cancel notifies attendees); cancelling a personal non-meeting appointment is a
// plain DELETE and lives on IReplicaGraphClient.DeleteEventAsync — the endpoint decides.
public interface IEventResponder
{
    Task RespondAsync(string eventId, RespondAction action, string? comment, CancellationToken ct = default);
    Task CancelMeetingAsync(string eventId, string? comment, CancellationToken ct = default);
}
