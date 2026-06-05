using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using ZyncMaster.App.Bridge;
using ZyncMaster.App.Configuration;
using ZyncMaster.Core;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.App.Tests;

// Covers EngineActions.GenerateTxtAsync param plumbing (item 6) and CreateCalendarAsync (item 4c).
// Every external boundary is a mock: the CalExport runner, the pairs client, the identity cache,
// and the save dialog. No server, no Outlook, no Graph.
public class EngineActionsExportAndCalendarTests
{
    private static EngineActions Build(
        Mock<ICalExportRunner> runner,
        Mock<IPairsClient> pairs,
        Mock<IIdentityTokenCache> identityCache,
        Func<string, Task<string?>> saveDialog,
        EngineSettings? settings = null,
        IOutlookComProbe? comProbe = null,
        IDeviceKeyStore? keyStore = null,
        ICalendarSource? comSource = null,
        IClock? clock = null)
    {
        settings ??= new EngineSettings { ServerBaseUrl = "https://server.test" };

        var keys = keyStore ?? new Mock<IDeviceKeyStore>().Object;
        var pairing = new PairingService(
            new Mock<IPairingClient>().Object,
            new Mock<IBrowserLauncher>().Object,
            keys,
            settings);
        var sync = new SyncEngine(
            keys,
            new Mock<ICalendarSource>().Object,
            new Mock<ISyncClient>().Object,
            new Mock<IClock>().Object,
            settings);
        var identity = new IdentityLoginService(
            new Mock<IIdentityServerClient>().Object,
            new Mock<IIdentityTokenCache>().Object,
            () => new Mock<IIdentityLoopback>().Object,
            new Mock<ISystemBrowser>().Object,
            "https://server.test");
        var calendarConnect = new CalendarConnectService(
            new Mock<ICalendarServerClient>().Object,
            new Mock<IIdentityTokenCache>().Object,
            () => new Mock<IIdentityLoopback>().Object,
            new Mock<ISystemBrowser>().Object);

        return new EngineActions(
            keys,
            pairing,
            sync,
            new Mock<ISettingsRepository<AppSettings>>().Object,
            new AppSettingsResolver(),
            "settings.json",
            pairs.Object,
            identityCache.Object,
            new BasicTxtExporter(runner.Object),
            new Mock<IAutoStartManager>().Object,
            settings,
            saveDialog,
            "host.exe",
            identity,
            calendarConnect,
            comProbe ?? new Mock<IOutlookComProbe>().Object,
            comSource ?? new Mock<ICalendarSource>().Object,
            clock ?? new Mock<IClock>().Object,
            new HttpClient());
    }

    private static Mock<IIdentityTokenCache> SignedIn(string token = "bearer-1")
    {
        var cache = new Mock<IIdentityTokenCache>();
        cache.Setup(c => c.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdentityTokens(token, "refresh"));
        return cache;
    }

    // ---------------- Item 6: GenerateTxtAsync param plumbing ----------------

    [Fact]
    public async Task GenerateTxt_passes_year_month_and_includeCancelled_from_request_json()
    {
        var runner = new Mock<ICalExportRunner>();
        runner.Setup(r => r.ExportSimpleAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var actions = Build(
            runner,
            new Mock<IPairsClient>(),
            SignedIn(),
            _ => Task.FromResult<string?>(@"C:\out\export.txt"));

        var json = "{\"year\":2024,\"month\":3,\"includeCancelled\":false,\"calendarNames\":[\"Work\"]}";
        var path = await actions.GenerateTxtAsync(json, CancellationToken.None);

        path.Should().Be(@"C:\out\export.txt");
        runner.Verify(r => r.ExportSimpleAsync(
            2024, 3,
            It.Is<IReadOnlyList<string>?>(c => c != null && c.Count == 1 && c[0] == "Work"),
            false, @"C:\out\export.txt", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateTxt_defaults_to_current_month_and_includeCancelled_when_payload_blank()
    {
        var runner = new Mock<ICalExportRunner>();
        runner.Setup(r => r.ExportSimpleAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var now = DateTime.Now;
        var actions = Build(
            runner,
            new Mock<IPairsClient>(),
            SignedIn(),
            _ => Task.FromResult<string?>(@"C:\out\export.txt"));

        await actions.GenerateTxtAsync("", CancellationToken.None);

        runner.Verify(r => r.ExportSimpleAsync(
            now.Year, now.Month, It.IsAny<IReadOnlyList<string>?>(),
            true, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateTxt_degrades_to_current_month_when_payload_is_malformed_json()
    {
        var runner = new Mock<ICalExportRunner>();
        runner.Setup(r => r.ExportSimpleAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var now = DateTime.Now;
        var actions = Build(
            runner,
            new Mock<IPairsClient>(),
            SignedIn(),
            _ => Task.FromResult<string?>(@"C:\out\export.txt"));

        // Non-whitespace but invalid JSON must not throw; it falls back to the current month.
        var path = await actions.GenerateTxtAsync("{not valid json", CancellationToken.None);

        path.Should().Be(@"C:\out\export.txt");
        runner.Verify(r => r.ExportSimpleAsync(
            now.Year, now.Month, It.IsAny<IReadOnlyList<string>?>(),
            true, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateTxt_returns_null_and_skips_export_when_save_dialog_cancelled()
    {
        var runner = new Mock<ICalExportRunner>();
        var actions = Build(
            runner,
            new Mock<IPairsClient>(),
            SignedIn(),
            _ => Task.FromResult<string?>(null)); // user cancelled

        var path = await actions.GenerateTxtAsync("{\"year\":2024,\"month\":3}", CancellationToken.None);

        path.Should().BeNull();
        runner.Verify(r => r.ExportSimpleAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(),
            It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("{\"month\":0}")]
    [InlineData("{\"month\":13}")]
    public async Task GenerateTxt_rejects_out_of_range_month(string json)
    {
        var actions = Build(
            new Mock<ICalExportRunner>(),
            new Mock<IPairsClient>(),
            SignedIn(),
            _ => Task.FromResult<string?>(@"C:\out\export.txt"));

        Func<Task> act = () => actions.GenerateTxtAsync(json, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GenerateTxt_null_request_throws()
    {
        var actions = Build(
            new Mock<ICalExportRunner>(),
            new Mock<IPairsClient>(),
            SignedIn(),
            _ => Task.FromResult<string?>(@"C:\out\export.txt"));

        Func<Task> act = () => actions.GenerateTxtAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ---------------- Item 4c: CreateCalendarAsync ----------------

    [Fact]
    public async Task CreateCalendar_sends_bearer_account_and_name_and_returns_calendar()
    {
        var pairs = new Mock<IPairsClient>();
        pairs.Setup(p => p.CreateCalendarAsync("bearer-1", "acc-1", "Travel", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CalendarInfo { Id = "cal-new", DisplayName = "Travel" });

        var actions = Build(
            new Mock<ICalExportRunner>(),
            pairs,
            SignedIn(),
            _ => Task.FromResult<string?>(null));

        var result = await actions.CreateCalendarAsync("acc-1", "Travel", CancellationToken.None);

        result.Id.Should().Be("cal-new");
        result.DisplayName.Should().Be("Travel");
        pairs.Verify(p => p.CreateCalendarAsync("bearer-1", "acc-1", "Travel", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateCalendar_trims_name_before_sending()
    {
        var pairs = new Mock<IPairsClient>();
        pairs.Setup(p => p.CreateCalendarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CalendarInfo { Id = "cal-new", DisplayName = "Travel" });

        var actions = Build(
            new Mock<ICalExportRunner>(),
            pairs,
            SignedIn(),
            _ => Task.FromResult<string?>(null));

        await actions.CreateCalendarAsync("acc-1", "  Travel  ", CancellationToken.None);

        pairs.Verify(p => p.CreateCalendarAsync("bearer-1", "acc-1", "Travel", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateCalendar_blank_name_throws_and_never_calls_server()
    {
        var pairs = new Mock<IPairsClient>();
        var actions = Build(
            new Mock<ICalExportRunner>(),
            pairs,
            SignedIn(),
            _ => Task.FromResult<string?>(null));

        Func<Task> act = () => actions.CreateCalendarAsync("acc-1", "   ", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        pairs.Verify(p => p.CreateCalendarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateCalendar_requires_sign_in()
    {
        var cache = new Mock<IIdentityTokenCache>();
        cache.Setup(c => c.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdentityTokens?)null); // signed out

        var pairs = new Mock<IPairsClient>();
        var actions = Build(
            new Mock<ICalExportRunner>(),
            pairs,
            cache,
            _ => Task.FromResult<string?>(null));

        Func<Task> act = () => actions.CreateCalendarAsync("acc-1", "Travel", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        pairs.Verify(p => p.CreateCalendarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------- ExportSourceTxtAsync (Graph branch of the per-pair export) ----------------

    [Fact]
    public async Task ExportSourceTxt_fetches_server_text_and_writes_to_chosen_path()
    {
        var pairs = new Mock<IPairsClient>();
        pairs.Setup(p => p.ExportSourceTxtAsync("bearer-1", "p1", 2026, 6, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync("the .txt content");

        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"zync-export-{System.Guid.NewGuid():N}.txt");
        var actions = Build(
            new Mock<ICalExportRunner>(),
            pairs,
            SignedIn(),
            _ => Task.FromResult<string?>(tmp));

        try
        {
            var json = "{\"pairId\":\"p1\",\"year\":2026,\"month\":6,\"includeCancelled\":true}";
            var path = await actions.ExportSourceTxtAsync(json, CancellationToken.None);

            path.Should().Be(tmp);
            (await System.IO.File.ReadAllTextAsync(tmp)).Should().Be("the .txt content");
            pairs.Verify(p => p.ExportSourceTxtAsync("bearer-1", "p1", 2026, 6, true, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public async Task ExportSourceTxt_returns_null_and_writes_nothing_when_save_cancelled()
    {
        var pairs = new Mock<IPairsClient>();
        pairs.Setup(p => p.ExportSourceTxtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("content");

        var actions = Build(
            new Mock<ICalExportRunner>(),
            pairs,
            SignedIn(),
            _ => Task.FromResult<string?>(null)); // user cancelled the save dialog

        var json = "{\"pairId\":\"p1\",\"year\":2026,\"month\":6,\"includeCancelled\":true}";
        var path = await actions.ExportSourceTxtAsync(json, CancellationToken.None);

        path.Should().BeNull();
        // The save dialog is asked FIRST, so a cancel must NOT waste a Graph read on the server.
        pairs.Verify(p => p.ExportSourceTxtAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExportSourceTxt_requires_pairId()
    {
        var actions = Build(
            new Mock<ICalExportRunner>(),
            new Mock<IPairsClient>(),
            SignedIn(),
            _ => Task.FromResult<string?>("x.txt"));

        Func<Task> act = () => actions.ExportSourceTxtAsync("{\"year\":2026,\"month\":6}", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExportSourceTxt_rejects_invalid_month()
    {
        var actions = Build(
            new Mock<ICalExportRunner>(),
            new Mock<IPairsClient>(),
            SignedIn(),
            _ => Task.FromResult<string?>("x.txt"));

        Func<Task> act = () => actions.ExportSourceTxtAsync("{\"pairId\":\"p1\",\"year\":2026,\"month\":13}", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExportSourceTxt_requires_sign_in()
    {
        var cache = new Mock<IIdentityTokenCache>();
        cache.Setup(c => c.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IdentityTokens?)null);

        var pairs = new Mock<IPairsClient>();
        var actions = Build(
            new Mock<ICalExportRunner>(),
            pairs,
            cache,
            _ => Task.FromResult<string?>("x.txt"));

        Func<Task> act = () => actions.ExportSourceTxtAsync("{\"pairId\":\"p1\",\"year\":2026,\"month\":6}", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        pairs.Verify(p => p.ExportSourceTxtAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------- GetCapabilitiesAsync ----------------

    [Fact]
    public async Task GetCapabilities_reports_com_available_when_probe_true()
    {
        var probe = new Mock<IOutlookComProbe>();
        probe.Setup(p => p.IsAvailable()).Returns(true);

        var actions = Build(
            new Mock<ICalExportRunner>(),
            new Mock<IPairsClient>(),
            SignedIn(),
            _ => Task.FromResult<string?>(null),
            comProbe: probe.Object);

        var caps = await actions.GetCapabilitiesAsync(CancellationToken.None);
        caps.OutlookCom.Should().BeTrue();
    }

    [Fact]
    public async Task GetCapabilities_reports_com_unavailable_when_probe_false()
    {
        var probe = new Mock<IOutlookComProbe>();
        probe.Setup(p => p.IsAvailable()).Returns(false);

        var actions = Build(
            new Mock<ICalExportRunner>(),
            new Mock<IPairsClient>(),
            SignedIn(),
            _ => Task.FromResult<string?>(null),
            comProbe: probe.Object);

        var caps = await actions.GetCapabilitiesAsync(CancellationToken.None);
        caps.OutlookCom.Should().BeFalse();
    }

    // ---------------- GAP 2: RunPairNowAsync COM-vs-Graph data path ----------------

    private static Mock<IDeviceKeyStore> KeyStore(string? key)
    {
        var keys = new Mock<IDeviceKeyStore>();
        keys.Setup(k => k.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(key);
        return keys;
    }

    private static SyncPair Pair(string id, string sourceProvider)
        => new SyncPair
        {
            Id = id,
            Name = id,
            State = "active",
            IntervalMin = 10,
            Source = new Endpoint { Provider = sourceProvider, CalendarId = "c", CalendarName = "C" },
            Destination = new Endpoint { Provider = "MicrosoftGraph", AccountRef = "a", CalendarId = "d", CalendarName = "D" },
        };

    [Fact]
    public async Task RunPairNow_ComPair_ReadsWindowAndPushes_NotRun()
    {
        var settings = new EngineSettings { ServerBaseUrl = "https://server.test", SyncWindowDays = 14 };
        var now = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.UtcNow).Returns(now);

        var events = (IReadOnlyList<AppointmentRecord>)new[] { new AppointmentRecord { Id = "e1", Subject = "x" } };
        var comSource = new Mock<ICalendarSource>();
        comSource.Setup(s => s.ReadWindowAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);

        var pairs = new Mock<IPairsClient>();
        pairs.Setup(p => p.ListPairsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SyncPair>)new[] { Pair("p1", "OutlookCom") });
        pairs.Setup(p => p.PushPairAsync("device-key", "p1", events, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MirrorResult { Created = 1 });

        var actions = Build(
            new Mock<ICalExportRunner>(),
            pairs,
            SignedIn("bearer-1"),
            _ => Task.FromResult<string?>(null),
            settings: settings,
            keyStore: KeyStore("device-key").Object,
            comSource: comSource.Object,
            clock: clock.Object);

        var result = await actions.RunPairNowAsync("p1", CancellationToken.None);

        result.Created.Should().Be(1);
        // FIX 2 — the COM read-from is snapped to today 00:00 UTC so it matches the server's sweep
        // window lower bound (not the current instant `now` = 09:00). The clock is 2026-06-01 09:00Z,
        // so the read window is [2026-06-01 00:00Z, +14 days].
        var expectedFrom = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        comSource.Verify(s => s.ReadWindowAsync(expectedFrom, expectedFrom.AddDays(14), It.IsAny<CancellationToken>()), Times.Once);
        pairs.Verify(p => p.PushPairAsync("device-key", "p1", events, It.IsAny<CancellationToken>()), Times.Once);
        pairs.Verify(p => p.RunPairAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunPairNow_GraphPair_CallsRunPair_NotPush()
    {
        var pairs = new Mock<IPairsClient>();
        pairs.Setup(p => p.ListPairsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SyncPair>)new[] { Pair("p1", "MicrosoftGraph") });
        pairs.Setup(p => p.RunPairAsync("device-key", "p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MirrorResult { Updated = 1 });

        var comSource = new Mock<ICalendarSource>();

        var actions = Build(
            new Mock<ICalExportRunner>(),
            pairs,
            SignedIn("bearer-1"),
            _ => Task.FromResult<string?>(null),
            keyStore: KeyStore("device-key").Object,
            comSource: comSource.Object);

        var result = await actions.RunPairNowAsync("p1", CancellationToken.None);

        result.Updated.Should().Be(1);
        pairs.Verify(p => p.RunPairAsync("device-key", "p1", It.IsAny<CancellationToken>()), Times.Once);
        pairs.Verify(p => p.PushPairAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<AppointmentRecord>>(), It.IsAny<CancellationToken>()), Times.Never);
        comSource.Verify(s => s.ReadWindowAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunPairNow_UnknownPair_Throws()
    {
        var pairs = new Mock<IPairsClient>();
        pairs.Setup(p => p.ListPairsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SyncPair>)Array.Empty<SyncPair>());

        var actions = Build(
            new Mock<ICalExportRunner>(),
            pairs,
            SignedIn("bearer-1"),
            _ => Task.FromResult<string?>(null),
            keyStore: KeyStore("device-key").Object);

        Func<Task> act = () => actions.RunPairNowAsync("missing", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RunPairNow_NotPaired_Throws()
    {
        var actions = Build(
            new Mock<ICalExportRunner>(),
            new Mock<IPairsClient>(),
            SignedIn("bearer-1"),
            _ => Task.FromResult<string?>(null),
            keyStore: KeyStore(null).Object);

        Func<Task> act = () => actions.RunPairNowAsync("p1", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
