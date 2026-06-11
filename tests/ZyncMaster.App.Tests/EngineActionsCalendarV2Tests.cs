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

// Covers the Calendar v2 bridge actions on EngineActions: raw-JSON pass-through to
// ICalendarV2Client with the identity bearer, the date/identity validation, the
// two-segment {accountId}/{eventId} extraction for replicas, and the create-vs-update
// routing for prefix rules. Every external boundary is a fake — no server, no HTTP.
public class EngineActionsCalendarV2Tests
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
        IClock? clock = null,
        ICalendarV2Client? calendarV2 = null)
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
            runner.Object,
            clock ?? new Mock<IClock>().Object,
            new HttpClient(),
            ZyncMaster.Core.NullAppLogger.Instance,
            calendarV2: calendarV2);
    }

    private static Mock<IIdentityTokenCache> SignedIn(string token = "bearer-1")
    {
        var cache = new Mock<IIdentityTokenCache>();
        cache.Setup(c => c.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdentityTokens(token, "refresh"));
        return cache;
    }

    // Records every call so the tests assert the pass-through plumbing without any HTTP.
    private sealed class FakeCalendarV2Client : ICalendarV2Client
    {
        public string? LastBearer; public string? LastDate; public string? LastAccountId;
        public string? LastEventId; public string? LastBody; public string? LastRuleId;
        public string DayJson = "{\"date\":\"2026-06-10\",\"accounts\":[]}";
        public string RulesJson = "[]";
        public string EchoJson = "{\"ok\":true}";
        public int CreateRuleCalls; public int UpdateRuleCalls; public int DeleteRuleCalls;

        public Task<string> GetDayAsync(string bearer, string dateIso, CancellationToken ct)
        { LastBearer = bearer; LastDate = dateIso; return Task.FromResult(DayJson); }
        public Task<string> CreateEventAsync(string bearer, string requestJson, CancellationToken ct)
        { LastBearer = bearer; LastBody = requestJson; return Task.FromResult(EchoJson); }
        public Task<string> CreateReplicasAsync(string bearer, string accountId, string eventId, string requestJson, CancellationToken ct)
        { LastBearer = bearer; LastAccountId = accountId; LastEventId = eventId; LastBody = requestJson; return Task.FromResult(EchoJson); }
        public Task<string> ListPrefixRulesAsync(string bearer, CancellationToken ct)
        { LastBearer = bearer; return Task.FromResult(RulesJson); }
        public Task<string> CreatePrefixRuleAsync(string bearer, string ruleJson, CancellationToken ct)
        { CreateRuleCalls++; LastBody = ruleJson; return Task.FromResult(EchoJson); }
        public Task<string> UpdatePrefixRuleAsync(string bearer, string ruleId, string ruleJson, CancellationToken ct)
        { UpdateRuleCalls++; LastRuleId = ruleId; LastBody = ruleJson; return Task.FromResult(EchoJson); }
        public Task DeletePrefixRuleAsync(string bearer, string ruleId, CancellationToken ct)
        { DeleteRuleCalls++; LastRuleId = ruleId; return Task.CompletedTask; }
    }

    [Fact]
    public async Task GetCalendarDay_passes_bearer_and_trimmed_date_and_returns_raw_json()
    {
        var fake = new FakeCalendarV2Client();
        var actions = Build(new Mock<ICalExportRunner>(), new Mock<IPairsClient>(), SignedIn("tok-9"),
            _ => Task.FromResult<string?>(null), calendarV2: fake);

        var json = await actions.GetCalendarDayAsync("  2026-06-10  ", CancellationToken.None);

        json.Should().Be(fake.DayJson);
        fake.LastBearer.Should().Be("tok-9");
        fake.LastDate.Should().Be("2026-06-10");
    }

    [Fact]
    public async Task GetCalendarDay_rejects_a_date_that_is_not_yyyy_MM_dd()
    {
        var actions = Build(new Mock<ICalExportRunner>(), new Mock<IPairsClient>(), SignedIn(),
            _ => Task.FromResult<string?>(null), calendarV2: new FakeCalendarV2Client());

        var act = () => actions.GetCalendarDayAsync("10/06/2026", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*yyyy-MM-dd*");
    }

    [Fact]
    public async Task GetCalendarDay_requires_a_signed_in_identity()
    {
        var signedOut = new Mock<IIdentityTokenCache>(); // LoadAsync -> null
        var actions = Build(new Mock<ICalExportRunner>(), new Mock<IPairsClient>(), signedOut,
            _ => Task.FromResult<string?>(null), calendarV2: new FakeCalendarV2Client());

        var act = () => actions.GetCalendarDayAsync("2026-06-10", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*signed in*");
    }

    [Fact]
    public async Task CalendarV2_actions_report_not_available_when_no_client_is_wired()
    {
        var actions = Build(new Mock<ICalExportRunner>(), new Mock<IPairsClient>(), SignedIn(),
            _ => Task.FromResult<string?>(null)); // calendarV2: null

        var act = () => actions.GetCalendarDayAsync("2026-06-10", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not available*");
    }

    [Fact]
    public async Task CreateEventReplicas_extracts_the_two_segment_identity_and_forwards_only_destinations()
    {
        var fake = new FakeCalendarV2Client();
        var actions = Build(new Mock<ICalExportRunner>(), new Mock<IPairsClient>(), SignedIn(),
            _ => Task.FromResult<string?>(null), calendarV2: fake);

        var request = "{\"accountId\":\"acc-1\",\"eventId\":\"evt-1\",\"destinations\":[{\"accountId\":\"a\",\"calendarId\":\"c\",\"title\":\"Busy\"}]}";
        await actions.CreateEventReplicasAsync(request, CancellationToken.None);

        fake.LastAccountId.Should().Be("acc-1");
        fake.LastEventId.Should().Be("evt-1");
        fake.LastBody.Should().Contain("\"destinations\"").And.Contain("\"Busy\"").And.NotContain("eventId");
    }

    [Fact]
    public async Task CreateEventReplicas_rejects_missing_identity_or_empty_destinations()
    {
        var actions = Build(new Mock<ICalExportRunner>(), new Mock<IPairsClient>(), SignedIn(),
            _ => Task.FromResult<string?>(null), calendarV2: new FakeCalendarV2Client());

        await ((Func<Task>)(() => actions.CreateEventReplicasAsync("{\"eventId\":\"e\",\"destinations\":[{}]}", CancellationToken.None)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*accountId*");
        await ((Func<Task>)(() => actions.CreateEventReplicasAsync("{\"accountId\":\"a\",\"destinations\":[{}]}", CancellationToken.None)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*eventId*");
        await ((Func<Task>)(() => actions.CreateEventReplicasAsync("{\"accountId\":\"a\",\"eventId\":\"e\",\"destinations\":[]}", CancellationToken.None)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*destination*");
        await ((Func<Task>)(() => actions.CreateEventReplicasAsync("{not json", CancellationToken.None)))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SavePrefixRule_creates_without_id_and_updates_with_id()
    {
        var fake = new FakeCalendarV2Client();
        var actions = Build(new Mock<ICalExportRunner>(), new Mock<IPairsClient>(), SignedIn(),
            _ => Task.FromResult<string?>(null), calendarV2: fake);

        await actions.SavePrefixRuleAsync("{\"prefix\":\"Lunch\",\"maskTitle\":\"Lunch\"}", CancellationToken.None);
        await actions.SavePrefixRuleAsync("{\"id\":\"r-1\",\"prefix\":\"Gym\"}", CancellationToken.None);

        fake.CreateRuleCalls.Should().Be(1);
        fake.UpdateRuleCalls.Should().Be(1);
        fake.LastRuleId.Should().Be("r-1");
    }

    [Fact]
    public async Task DeletePrefixRule_validates_id_and_forwards_it()
    {
        var fake = new FakeCalendarV2Client();
        var actions = Build(new Mock<ICalExportRunner>(), new Mock<IPairsClient>(), SignedIn(),
            _ => Task.FromResult<string?>(null), calendarV2: fake);

        await actions.DeletePrefixRuleAsync("r-2", CancellationToken.None);
        fake.DeleteRuleCalls.Should().Be(1);
        fake.LastRuleId.Should().Be("r-2");

        await ((Func<Task>)(() => actions.DeletePrefixRuleAsync("", CancellationToken.None)))
            .Should().ThrowAsync<ArgumentNullException>();
    }
}
