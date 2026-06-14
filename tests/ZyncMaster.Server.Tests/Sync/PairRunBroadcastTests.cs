using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Newtonsoft.Json.Linq;
using ZyncMaster.Core;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// Endpoint-level proof that a recorded pair run / a pair-set change pushes a WS frame to the user's
// OTHER live sessions through the real DI graph. The Sync module had NO push channel before; these
// tests drive the actual /api/pairs endpoints and assert the SyncBroadcaster (resolved from the
// container, sharing the clipboard presence registry) fans the run out to a peer socket we register
// directly in that registry, while excluding the device that triggered it and never crossing users.
public class PairRunBroadcastTests : IClassFixture<ServerTestFactory>
{
    private readonly ServerTestFactory _factory;

    public PairRunBroadcastTests(ServerTestFactory factory) => _factory = factory;

    // A live, capturing socket the broadcaster will treat as Open and send to.
    private sealed class CapturingWebSocket : WebSocket
    {
        public List<string> Sent { get; } = new();

        public override WebSocketState State => WebSocketState.Open;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            lock (Sent) Sent.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class RecordingWriter : ICalendarWriter
    {
        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarOption>>(Array.Empty<CalendarOption>());

        public Task<CalendarOption> CreateCalendarAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(new CalendarOption { Id = "new", DisplayName = name });

        public Task<MirrorResult> MirrorAsync(
            string calendarId, IReadOnlyList<AppointmentRecord> records, int reminderMinutes,
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default, string pairId = "") =>
            Task.FromResult(new MirrorResult { Created = records.Count });
    }

    private sealed class RecordingReader : ICalendarReader
    {
        public List<AppointmentRecord> Window { get; init; } = new();

        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarOption>>(Array.Empty<CalendarOption>());

        public Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
            CancellationToken ct = default, bool preserveLocalTime = false) =>
            Task.FromResult<IReadOnlyList<AppointmentRecord>>(Window);
    }

    private static WebApplicationFactory<Program> Build(RecordingReader? reader) =>
        new ServerTestFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ProviderRegistry>();
                services.AddSingleton(new ProviderRegistry(
                    readerFactory: _ => reader ?? new RecordingReader(),
                    writerFactory: _ => new RecordingWriter()));

                services.RemoveAll<IConnectedAccountStore>();
                services.AddSingleton<IConnectedAccountStore>(_ =>
                {
                    var store = new DataProtectionConnectedAccountStore(DataProtectionProvider.Create("tests"));
                    store.SetAsync("default", "rt").GetAwaiter().GetResult();
                    return store;
                });

                services.RemoveAll<ISyncPairStore>();
                services.AddSingleton<ISyncPairStore, InMemorySyncPairStore>();
            });
        });

    // Register a device with a KNOWN id under the default test user, returning an authed client whose
    // api-key principal carries that deviceId. The pushing device's id becomes the broadcast origin.
    private static async Task<HttpClient> AuthedClientAsync(WebApplicationFactory<Program> factory, string deviceId)
    {
        var store = factory.Services.GetRequiredService<IDeviceStore>();
        var key = ApiKeyGenerator.Generate();
        await store.AddAsync(new Device
        {
            Id = deviceId,
            Name = "Origin",
            ApiKeyHash = ApiKeyHasher.Hash(key),
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static CapturingWebSocket RegisterSession(WebApplicationFactory<Program> factory, string userId, string deviceId)
    {
        var reg = factory.Services.GetRequiredService<ClipboardConnectionRegistry>();
        var socket = new CapturingWebSocket();
        reg.Add(new ClipboardConnection { UserId = userId, DeviceId = deviceId, Socket = socket });
        return socket;
    }

    private static async Task<string> SeedPairAsync(WebApplicationFactory<Program> factory, string sourceProvider)
    {
        var store = factory.Services.GetRequiredService<ISyncPairStore>();
        var id = Guid.NewGuid().ToString("N");
        await store.AddAsync(new SyncPair
        {
            Id = id,
            Name = "Pair",
            Source = new Endpoint { Provider = sourceProvider, AccountRef = "default", CalendarId = "src-cal" },
            Destination = new Endpoint { Provider = "MicrosoftGraph", AccountRef = "default", CalendarId = "dst-cal" },
            IntervalMin = 15,
        });
        return id;
    }

    private static AppointmentRecord MakeEvent(string id) =>
        new() { Id = id, Subject = id, StartOffset = DateTimeOffset.UtcNow.AddDays(1), EndOffset = DateTimeOffset.UtcNow.AddDays(1).AddHours(1), StartTimeZoneId = "UTC" };

    private const string User = DefaultCurrentUserAccessor.DefaultUserId;

    [Fact]
    public async Task Push_broadcasts_pair_run_to_peer_session_excluding_origin_device()
    {
        using var factory = Build(null);
        var client = await AuthedClientAsync(factory, deviceId: "d-origin");
        var peer = RegisterSession(factory, User, "d-peer");
        var origin = RegisterSession(factory, User, "d-origin");
        var id = await SeedPairAsync(factory, "OutlookCom");

        var resp = await client.PostAsJsonAsync($"/api/pairs/{id}/push", new
        {
            events = new[] { MakeEvent("a"), MakeEvent("b") },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // The peer's open session gets the run live; the device that pushed it does not (it already
        // has the result in the HTTP response).
        peer.Sent.Should().ContainSingle();
        origin.Sent.Should().BeEmpty();

        var frame = JObject.Parse(peer.Sent[0]);
        frame["type"]!.Value<string>().Should().Be("pair-run");
        frame["pairId"]!.Value<string>().Should().Be(id);
        frame["lastResult"]!["created"]!.Value<int>().Should().Be(2);
        frame["lastRunUtc"]!.Value<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Run_broadcasts_pair_run_to_peer_session()
    {
        using var factory = Build(new RecordingReader { Window = { MakeEvent("x"), MakeEvent("y"), MakeEvent("z") } });
        var client = await AuthedClientAsync(factory, deviceId: "d-origin");
        var peer = RegisterSession(factory, User, "d-peer");
        var id = await SeedPairAsync(factory, "MicrosoftGraph");

        var resp = await client.PostAsync($"/api/pairs/{id}/run", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        peer.Sent.Should().ContainSingle();
        var frame = JObject.Parse(peer.Sent[0]);
        frame["type"]!.Value<string>().Should().Be("pair-run");
        frame["pairId"]!.Value<string>().Should().Be(id);
        frame["lastResult"]!["created"]!.Value<int>().Should().Be(3);
    }

    [Fact]
    public async Task Push_does_not_broadcast_to_another_users_session()
    {
        using var factory = Build(null);
        var client = await AuthedClientAsync(factory, deviceId: "d-origin");
        var foreign = RegisterSession(factory, "another-user", "x-1");
        var id = await SeedPairAsync(factory, "OutlookCom");

        var resp = await client.PostAsJsonAsync($"/api/pairs/{id}/push", new
        {
            events = new[] { MakeEvent("a") },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        foreign.Sent.Should().BeEmpty("a run must never leak to another user's live session");
    }

    // Cookie-gated variant: like Build but also registers the fake identity token service so the real
    // /connect → /connect/callback OAuth flow (driven by CookieAuthHelper) succeeds and mints a session
    // cookie for a fresh non-default user. The connected-account store is seeded with the "default"
    // account so createPair's account-ownership validation passes.
    private static WebApplicationFactory<Program> BuildCookie() =>
        new ServerTestFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMicrosoftTokenService>();
                services.AddSingleton<IMicrosoftTokenService>(
                    new CookieAuthHelper.FakeIdentityTokenService { Upn = "" });

                services.RemoveAll<ProviderRegistry>();
                services.AddSingleton(new ProviderRegistry(
                    readerFactory: _ => new RecordingReader(),
                    writerFactory: _ => new RecordingWriter()));

                services.RemoveAll<IConnectedAccountStore>();
                services.AddSingleton<IConnectedAccountStore>(_ =>
                {
                    var store = new DataProtectionConnectedAccountStore(DataProtectionProvider.Create("tests"));
                    store.SetAsync("default", "rt").GetAwaiter().GetResult();
                    return store;
                });

                services.RemoveAll<ISyncPairStore>();
                services.AddSingleton<ISyncPairStore, InMemorySyncPairStore>();
            });
        });

    // The pair-CRUD endpoints are cookie-gated, so sign in through the real OAuth flow (which creates
    // a fresh non-default user) and resolve THAT user's id from the DB so the peer session can be
    // registered under the same scope the cookie request will broadcast to.
    private static async Task<(HttpClient client, string userId)> CookieClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = await CookieAuthHelper.SignInAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMaster.Server.Data.ZyncMasterDbContext>();
        var signedIn = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(
            db.Users, u => u.Id != DefaultCurrentUserAccessor.DefaultUserId);
        return (client, signedIn.Id);
    }

    [Fact]
    public async Task Create_pair_broadcasts_pairs_changed_to_peer_session()
    {
        using var factory = BuildCookie();
        var (client, userId) = await CookieClientAsync(factory);
        var peer = RegisterSession(factory, userId, "d-peer");

        var resp = await client.PostAsJsonAsync("/api/pairs", new
        {
            name = "New pair",
            source = new { provider = "OutlookCom", accountRef = "default", calendarId = "src" },
            destination = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "dst" },
            intervalMin = 15,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        peer.Sent.Should().ContainSingle();
        JObject.Parse(peer.Sent[0])["type"]!.Value<string>().Should().Be("pairs-changed");
    }

    [Fact]
    public async Task Delete_pair_broadcasts_pairs_changed_to_peer_session()
    {
        using var factory = BuildCookie();
        var (client, userId) = await CookieClientAsync(factory);
        var peer = RegisterSession(factory, userId, "d-peer");

        // Create the pair via the cookie client so it is owned by the signed-in user, then delete it.
        var created = await client.PostAsJsonAsync("/api/pairs", new
        {
            name = "Doomed pair",
            source = new { provider = "OutlookCom", accountRef = "default", calendarId = "src" },
            destination = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "dst" },
            intervalMin = 15,
        });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = JObject.Parse(await created.Content.ReadAsStringAsync())["id"]!.Value<string>();
        peer.Sent.Clear(); // drop the create's pairs-changed; we assert on the delete's frame.

        var resp = await client.DeleteAsync($"/api/pairs/{id}");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        peer.Sent.Should().ContainSingle();
        JObject.Parse(peer.Sent[0])["type"]!.Value<string>().Should().Be("pairs-changed");
    }

    // The VPS cron path (POST /api/sync/run-due) runs a due Graph->Graph pair under its OWNER and must
    // also fan the recorded run out to that owner's live sessions — this is the "a run on the VPS reaches
    // this machine" case from the diagnosis. We seed a due pair for a user with no active lease (so cron
    // does not skip it as covered), register a peer session for that user, trigger run-due, and assert
    // the peer got the pair-run frame.
    private const string CronSecret = "cron-broadcast-secret";

    private static WebApplicationFactory<Program> BuildCron() =>
        new ServerTestFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Server:CronTriggerSecret", CronSecret);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ProviderRegistry>();
                services.AddSingleton(new ProviderRegistry(
                    readerFactory: _ => new RecordingReader { Window = { MakeEvent("c1") } },
                    writerFactory: _ => new RecordingWriter()));
            });
        });

    [Fact]
    public async Task Cron_run_due_broadcasts_pair_run_to_owner_session()
    {
        const string owner = "cron-owner";
        using var factory = BuildCron();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZyncMaster.Server.Data.ZyncMasterDbContext>();
            db.Users.Add(new ZyncMaster.Server.Data.UserRow
            {
                Id = owner, Provider = "local", Subject = owner, CreatedUtc = DateTimeOffset.UtcNow,
            });
            var protector = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()
                .CreateProtector("ZyncMaster.RefreshToken");
            db.ConnectedAccounts.Add(new ZyncMaster.Server.Data.ConnectedAccountRow
            {
                Id = owner + "|default",
                UserId = owner,
                Provider = "MicrosoftGraph",
                AccountRef = "default",
                EncryptedRefreshToken = protector.Protect("rt"),
                ConnectedUtc = DateTimeOffset.UtcNow,
            });
            db.SyncPairs.Add(new ZyncMaster.Server.Data.SyncPairRow
            {
                Id = Guid.NewGuid().ToString("N"),
                UserId = owner,
                Name = "Cron pair",
                SourceJson = TestEndpointJson.Serialize(new Endpoint
                {
                    Provider = "MicrosoftGraph", AccountRef = "default", CalendarId = "src-cron",
                }),
                DestinationJson = TestEndpointJson.Serialize(new Endpoint
                {
                    Provider = "MicrosoftGraph", AccountRef = "default", CalendarId = "dst-cron",
                }),
                IntervalMin = 15,
                State = "active",
                LastRunUtc = null,
            });
            await db.SaveChangesAsync();
        }

        var peer = RegisterSession(factory, owner, "owner-device");

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/sync/run-due")
        {
            Content = JsonContent.Create(new { }),
        };
        req.Headers.Add(SyncRunDueEndpoints.SecretHeader, CronSecret);
        var resp = await factory.CreateClient().SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        peer.Sent.Should().ContainSingle("the cron run must reach the owner's live session");
        var frame = JObject.Parse(peer.Sent[0]);
        frame["type"]!.Value<string>().Should().Be("pair-run");
        frame["lastRunUtc"]!.Value<string>().Should().NotBeNullOrEmpty();
    }
}
