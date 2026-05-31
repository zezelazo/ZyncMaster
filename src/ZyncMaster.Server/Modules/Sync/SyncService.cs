using Microsoft.Extensions.Options;
using ZyncMaster.Core;
using ZyncMaster.Graph;

namespace ZyncMaster.Server;

// Mirrors a device's appointment payload onto the CURRENT USER's connected Microsoft
// account calendar via Graph. The device request runs under ApiKey, so the current user is
// the device's owner (the ApiKey handler stamps the owner's userId claim). The connected
// account is resolved through IConnectedAccountStore, which is already user-scoped, so the
// account ref passed to the calendar-target factory derives from the owner's connected
// account (its refresh token) — never a global "default". A device whose owner has no
// connected account yields NoAccount = true (the endpoint maps that to a 409).
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

        // The account list is scoped to the current user (the device's owner). No account =>
        // the owner has not connected a Microsoft account yet.
        var accounts = await _accounts.ListAsync(ct).ConfigureAwait(false);
        if (accounts.Count == 0)
            return new SyncOutcome { NoAccount = true };

        // Use the owner's connected account ref so the token provider mirrors to THAT
        // account's calendar. UserPrincipalName carries the store key ("default" when the
        // UPN was unknown at connect time, else the real UPN).
        var accountRef = accounts[0].UserPrincipalName;
        var target = _targetFactory(accountRef);

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
                Failures = outcome.Failures.Select(f => f.ToString()).ToList(),
                Partial = outcome.Partial,
            },
        };
    }
}
