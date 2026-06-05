using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Core;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests;

// FIX 2 — the COM read window's lower bound MUST equal the server's sweep window lower bound
// (today 00:00 UTC), not the current instant `now`. Otherwise an event that starts between
// today 00:00 and `now` is inside the sweep window but absent from the pushed set, and the
// server's destructive sweep deletes it from the destination even though it still exists at the
// source — every single run.
public sealed class PairRunnerWindowTests
{
    [Fact]
    public void ReadWindow_floor_is_today_midnight_utc_not_now()
    {
        var now = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.Zero);

        var (from, to) = PairRunner.ReadWindow(now, windowDays: 14);

        from.Should().Be(new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero),
            "the read floor must match the server sweep floor (today 00:00 UTC), not the instant now");
        to.Should().Be(new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ReadWindow_normalizes_a_non_utc_now_to_its_utc_date()
    {
        // A `now` expressed with an offset must still snap to the UTC calendar date so it lines up
        // with the server's UtcNow.Date sweep floor.
        var now = new DateTimeOffset(2026, 6, 5, 1, 0, 0, TimeSpan.FromHours(3)); // = 2026-06-04T22:00Z

        var (from, _) = PairRunner.ReadWindow(now, windowDays: 7);

        from.Should().Be(new DateTimeOffset(2026, 6, 4, 0, 0, 0, TimeSpan.Zero));
    }

    // ── End-to-end through RunOnceAsync: an event at 02:00 today (between 00:00 and now=10:00)
    //    is inside the read window and IS pushed, so the server sweep cannot delete it. ──────────
    private sealed class RecordingSource : ICalendarSource
    {
        public DateTimeOffset LastFrom;
        public DateTimeOffset LastTo;
        public required IReadOnlyList<AppointmentRecord> All;

        public IReadOnlyList<string>? LastSelection;

        public Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
            DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlyList<string>? calendarNames, CancellationToken ct)
        {
            LastFrom = fromUtc;
            LastTo = toUtc;
            LastSelection = calendarNames;
            // Model the real OutlookComSource window filter: only events whose start is within
            // [from, to] are returned.
            IReadOnlyList<AppointmentRecord> inWindow = All
                .Where(e => e.StartOffset >= fromUtc && e.StartOffset <= toUtc)
                .ToList();
            return Task.FromResult(inWindow);
        }
    }

    private sealed class CapturingClient : IPairsClient
    {
        public IReadOnlyList<AppointmentRecord>? PushedEvents;

        public Task<MirrorResult> PushPairAsync(string apiKey, string id, IReadOnlyList<AppointmentRecord> events, CancellationToken ct)
        {
            PushedEvents = events;
            return Task.FromResult(new MirrorResult { Created = events.Count });
        }

        // Unused members for this test.
        public Task<IReadOnlyList<AccountInfo>> ListAccountsAsync(string bearer, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string bearer, string accountRef, CancellationToken ct) => throw new NotImplementedException();
        public Task<CalendarInfo> CreateCalendarAsync(string bearer, string accountRef, string name, CancellationToken ct) => throw new NotImplementedException();
        public Task<SyncPair> CreatePairAsync(string bearer, string name, Endpoint source, Endpoint destination, int intervalMin, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<SyncPair>> ListPairsAsync(string bearer, CancellationToken ct) => throw new NotImplementedException();
        public Task<SyncPair> UpdatePairAsync(string bearer, string id, string? name, int? intervalMin, string? state, CancellationToken ct, Endpoint? source = null, Endpoint? destination = null) => throw new NotImplementedException();
        public Task<string> ExportSourceTxtAsync(string bearer, string id, int year, int month, bool includeCancelled, CancellationToken ct) => throw new NotImplementedException();
        public Task<CleanupResult> CleanupDestinationAsync(string bearer, string id, Endpoint oldDestination, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountManagedAsync(string bearer, string id, Endpoint destination, CancellationToken ct) => throw new NotImplementedException();
        public Task DeletePairAsync(string bearer, string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<MirrorResult> RunPairAsync(string apiKey, string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<DateTimeOffset?> HeartbeatAsync(string apiKey, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> UnlinkAccountAsync(string bearer, string accountRef, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeviceInfo> GetDeviceMeAsync(string apiKey, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeviceInfo> RenameDeviceAsync(string apiKey, string name, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> CheckDeviceNameAvailableAsync(string apiKey, string name, CancellationToken ct) => throw new NotImplementedException();
    }

    [Fact]
    public async Task Event_starting_today_before_now_is_pushed_so_the_sweep_cannot_delete_it()
    {
        var now = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.Zero);
        var earlyToday = new DateTimeOffset(2026, 6, 5, 2, 0, 0, TimeSpan.Zero); // 02:00, before now

        var source = new RecordingSource
        {
            All = new[]
            {
                new AppointmentRecord { Id = "early", Subject = "Early", StartOffset = earlyToday, EndOffset = earlyToday.AddHours(1), StartTimeZoneId = "UTC" },
            },
        };
        var client = new CapturingClient();
        var pair = new SyncPair
        {
            Id = "p1",
            Name = "p1",
            Source = new Endpoint { Provider = "OutlookCom", CalendarId = "c", CalendarName = "C" },
            Destination = new Endpoint { Provider = "MicrosoftGraph", AccountRef = "a", CalendarId = "d", CalendarName = "D" },
        };

        await PairRunner.RunOnceAsync(client, source, pair, "key", now,
            new EngineSettings { ServerBaseUrl = "https://srv", SyncWindowDays = 14 }, CancellationToken.None);

        // The read floor is today 00:00 (the sweep floor), so 02:00 is inside the window and pushed.
        source.LastFrom.Should().Be(new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero));
        client.PushedEvents.Should().NotBeNull();
        client.PushedEvents!.Select(e => e.Id).Should().Contain("early",
            "an event starting today before `now` must be in the pushed set so the sweep keeps it");
    }
}
