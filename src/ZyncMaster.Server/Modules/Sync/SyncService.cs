using Microsoft.Extensions.Options;
using ZyncMaster.Core;
using ZyncMaster.Graph;

namespace ZyncMaster.Server;

// Mirrors a device's appointment payload onto the connected Microsoft account's
// calendar via Graph. Single-user model: there is at most one connected account,
// keyed under the store's "default" fallback, so the UPN passed to the calendar
// target factory is empty (ServerGraphTokenProvider + store both normalize to "default").
public sealed class SyncService
{
    private readonly IDeviceStore _devices;
    private readonly IConnectedAccountStore _accounts;
    private readonly Func<string, ICalendarTarget> _targetFactory;
    private readonly ISyncStateStore _state;
    private readonly IOptions<ServerOptions> _opts;

    public SyncService(
        IDeviceStore devices,
        IConnectedAccountStore accounts,
        Func<string, ICalendarTarget> targetFactory,
        ISyncStateStore state,
        IOptions<ServerOptions> opts)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _targetFactory = targetFactory ?? throw new ArgumentNullException(nameof(targetFactory));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
    }

    public async Task<SyncOutcome> SyncAsync(string deviceId, IReadOnlyList<AppointmentRecord> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(events);

        if (!await _accounts.HasAnyAsync(ct).ConfigureAwait(false))
            return new SyncOutcome { NoAccount = true };

        // Single connected account; empty UPN normalizes to the store's "default" key.
        var target = _targetFactory("");

        var device = await _devices.GetAsync(deviceId, ct).ConfigureAwait(false);
        string calendarId;
        if (!string.IsNullOrWhiteSpace(device?.TargetCalendarId))
        {
            calendarId = device!.TargetCalendarId!;
        }
        else
        {
            var cals = await target.ListCalendarsAsync(ct).ConfigureAwait(false);
            calendarId = cals.FirstOrDefault(c => c.IsDefault)?.Id ?? cals.First().Id;
        }

        var mirror = new CalendarMirror(target, new ImportPlanBuilder(), new EventDraftBuilder(new ParticipantBodyRenderer()));

        var now = DateTimeOffset.UtcNow;
        var outcome = await mirror
            .MirrorAsync(calendarId, events, 30, now, now.AddDays(_opts.Value.SyncWindowDays), ct)
            .ConfigureAwait(false);

        await _state.SetAsync(new SyncState
        {
            DeviceId = deviceId,
            LastSyncUtc = now,
            LastCreated = outcome.Created,
            LastUpdated = outcome.Updated,
            LastDeleted = outcome.Deleted,
        }, ct).ConfigureAwait(false);

        return new SyncOutcome
        {
            Response = new SyncResponse
            {
                Created = outcome.Created,
                Updated = outcome.Updated,
                Deleted = outcome.Deleted,
                Skipped = outcome.Skipped,
                Failures = outcome.Failures.ToList(),
            },
        };
    }
}
