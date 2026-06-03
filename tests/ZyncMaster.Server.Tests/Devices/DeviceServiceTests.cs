using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ZyncMaster.Server.Tests.Devices;

public class DeviceServiceTests
{
    private static (DeviceService svc, InMemoryDeviceStore store) Build(
        ICurrentUserAccessor? user = null, ServerOptions? options = null, IUserStore? users = null)
    {
        var store = new InMemoryDeviceStore();
        var svc = new DeviceService(
            store,
            user ?? new DefaultCurrentUserAccessor(),
            users ?? new StubUserStore(),
            new DeviceNameGenerator(),
            Options.Create(options ?? new ServerOptions()));
        return (svc, store);
    }

    private sealed class FixedUser : ICurrentUserAccessor
    {
        public FixedUser(string userId) => UserId = userId;
        public string UserId { get; }
    }

    // Minimal IUserStore double for name generation. Returns no user (so the generator falls back to
    // the userId as the account identifier) unless a row was seeded by id.
    private sealed class StubUserStore : IUserStore
    {
        private readonly System.Collections.Generic.Dictionary<string, ZyncMaster.Server.Data.UserRow> _byId = new();

        public StubUserStore(params ZyncMaster.Server.Data.UserRow[] users)
        {
            foreach (var u in users)
                _byId[u.Id] = u;
        }

        public Task<ZyncMaster.Server.Data.UserRow?> GetAsync(string id, System.Threading.CancellationToken ct = default)
            => Task.FromResult(_byId.TryGetValue(id, out var row) ? row : null);

        public Task<ZyncMaster.Server.Data.UserRow> UpsertAsync(
            string provider, string subject, string email, string displayName, System.Threading.CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ZyncMaster.Server.Data.UserRow> UpsertByLoginAsync(
            string provider, string providerSubject, string email, bool emailVerified, string displayName,
            System.Threading.CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ZyncMaster.Server.Data.UserRow?> TryLinkByEmailAsync(
            string provider, string providerSubject, string email, bool emailVerified, string displayName,
            System.Threading.CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public void Ctor_null_store_throws()
    {
        Action act = () => new DeviceService(
            null!, new DefaultCurrentUserAccessor(), new StubUserStore(), new DeviceNameGenerator(),
            Options.Create(new ServerOptions()));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task StartPairing_creates_pending_with_persisted_id_and_code()
    {
        var (svc, store) = Build();

        var result = await svc.StartPairingAsync("Laptop");

        result.PairingId.Should().NotBeNullOrWhiteSpace();
        result.Code.Should().NotBeNullOrWhiteSpace();

        var pending = await store.GetPendingAsync(result.PairingId);
        pending.Should().NotBeNull();
        pending!.Approved.Should().BeFalse();
        pending.Code.Should().Be(result.Code);
        pending.DeviceName.Should().Be("Laptop");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task StartPairing_empty_name_throws(string? name)
    {
        var (svc, _) = Build();
        Func<Task> act = () => svc.StartPairingAsync(name!);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Approve_marks_pending_approved_and_creates_verifiable_device()
    {
        var (svc, store) = Build();
        var start = await svc.StartPairingAsync("Phone");

        var ok = await svc.ApproveAsync(start.Code);

        ok.Should().BeTrue();
        var pending = await store.GetPendingAsync(start.PairingId);
        pending!.Approved.Should().BeTrue();
        pending.ApprovedDeviceId.Should().NotBeNullOrWhiteSpace();
        pending.OneTimeApiKey.Should().NotBeNullOrWhiteSpace();

        var devices = await store.ListAsync();
        devices.Should().HaveCount(1);
        var device = devices.Single();
        device.Id.Should().Be(pending.ApprovedDeviceId);
        device.Name.Should().Be("Phone");

        // §A-3 — the issued key is "keyId.secret"; only the SECRET half is hashed and the public
        // keyId is stored unhashed for the indexed lookup.
        ApiKeyGenerator.TryParse(pending.OneTimeApiKey!, out var keyId, out var secret).Should().BeTrue();
        device.KeyId.Should().Be(keyId);
        ApiKeyHasher.Verify(secret, device.ApiKeyHash).Should().BeTrue();
        ApiKeyHasher.Verify(pending.OneTimeApiKey!, device.ApiKeyHash).Should().BeFalse(
            "the hash is of the secret half only, never the composite key");
    }

    [Fact]
    public async Task Approve_binds_device_to_the_approving_user_not_default()
    {
        // §A-2 fix: a device created at approval must carry the REAL approving user, never the
        // seeded default. Approve runs under the panel cookie, so the ambient user is the approver.
        var (svc, store) = Build(user: new FixedUser("real-user"));
        var start = await svc.StartPairingAsync("Phone");

        await svc.ApproveAsync(start.Code);

        var device = (await store.ListAsync()).Single();
        device.UserId.Should().Be("real-user");
    }

    [Fact]
    public async Task Register_binds_device_to_token_user_and_sets_lease_and_key()
    {
        var options = new ServerOptions { DeviceLeaseTtlMinutes = 10 };
        var (svc, store) = Build(user: new FixedUser("token-user"), options: options);

        var before = DateTimeOffset.UtcNow;
        var result = await svc.RegisterAsync(new DeviceRegisterRequest(
            Name: "Workstation", Platform: "windows", HasOutlookCom: true, AppVersion: "1.2.3"));

        result.DeviceId.Should().NotBeNullOrWhiteSpace();
        result.ApiKey.Should().Contain(".");
        result.LeaseUntil.Should().BeOnOrAfter(before.AddMinutes(10).AddSeconds(-5));

        var device = (await store.ListAsync()).Single();
        device.UserId.Should().Be("token-user");
        device.Name.Should().Be("Workstation");
        device.Platform.Should().Be("windows");
        device.HasOutlookCom.Should().BeTrue();
        device.AppVersion.Should().Be("1.2.3");
        device.LeaseUntil.Should().NotBeNull();

        // The returned key authenticates: keyId stored unhashed, secret verifies against the hash.
        ApiKeyGenerator.TryParse(result.ApiKey, out var keyId, out var secret).Should().BeTrue();
        device.KeyId.Should().Be(keyId);
        ApiKeyHasher.Verify(secret, device.ApiKeyHash).Should().BeTrue();
    }

    [Fact]
    public async Task Register_with_blank_name_generates_geek_name_from_account_email()
    {
        var users = new StubUserStore(new ZyncMaster.Server.Data.UserRow
        {
            Id = "u-gen", PrimaryEmail = "zezelazo@msn.com",
        });
        var (svc, store) = Build(user: new FixedUser("u-gen"), users: users);

        await svc.RegisterAsync(new DeviceRegisterRequest(Name: ""));

        var device = (await store.ListAsync()).Single();
        device.Name.Should().NotBeNullOrWhiteSpace();
        device.Name.Should().NotBe("Device");
        device.Name.Should().EndWith("-zezelazo");
    }

    [Fact]
    public async Task Register_twice_without_name_yields_distinct_names_for_same_user()
    {
        var users = new StubUserStore(new ZyncMaster.Server.Data.UserRow
        {
            Id = "u-two", PrimaryEmail = "dupe@example.com",
        });
        var (svc, store) = Build(user: new FixedUser("u-two"), users: users);

        await svc.RegisterAsync(new DeviceRegisterRequest(Name: null!));
        await svc.RegisterAsync(new DeviceRegisterRequest(Name: "   "));

        var names = (await store.ListAsync()).Select(d => d.Name).ToList();
        names.Should().HaveCount(2);
        names.Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(2);
    }

    [Fact]
    public async Task GetMe_backfills_name_for_nameless_device_and_persists()
    {
        var users = new StubUserStore(new ZyncMaster.Server.Data.UserRow
        {
            Id = "u-heal", PrimaryEmail = "healme@example.com",
        });
        var (svc, store) = Build(user: new FixedUser("u-heal"), users: users);

        // Seed a legacy device with a blank name directly in the store.
        var legacy = new Device
        {
            Id = "legacy-1", UserId = "u-heal", Name = "", ApiKeyHash = "h",
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        await store.AddAsync(legacy);

        var result = await svc.GetMeAsync("legacy-1");

        result.Should().NotBeNull();
        result!.Name.Should().NotBeNullOrWhiteSpace();
        result.Name.Should().EndWith("-healme");

        // Persisted, not just returned.
        var stored = await store.GetAsync("legacy-1");
        stored!.Name.Should().Be(result.Name);
    }

    [Fact]
    public async Task GetMe_leaves_existing_name_untouched()
    {
        var (svc, store) = Build();
        var device = new Device
        {
            Id = "named-1", UserId = DefaultCurrentUserAccessor.DefaultUserId, Name = "Keep Me",
            ApiKeyHash = "h", CreatedUtc = DateTimeOffset.UtcNow,
        };
        await store.AddAsync(device);

        var result = await svc.GetMeAsync("named-1");

        result!.Name.Should().Be("Keep Me");
    }

    [Fact]
    public async Task Register_normalizes_unknown_platform_to_windows()
    {
        var (svc, store) = Build();
        await svc.RegisterAsync(new DeviceRegisterRequest(Name: "X", Platform: "android"));
        (await store.ListAsync()).Single().Platform.Should().Be("windows");
    }

    [Fact]
    public async Task Heartbeat_renews_lease_for_known_device()
    {
        var options = new ServerOptions { DeviceLeaseTtlMinutes = 10 };
        var (svc, store) = Build(options: options);
        var reg = await svc.RegisterAsync(new DeviceRegisterRequest(Name: "X"));

        var before = DateTimeOffset.UtcNow;
        var result = await svc.HeartbeatAsync(reg.DeviceId);

        result.Should().NotBeNull();
        result!.LeaseUntil.Should().BeOnOrAfter(before.AddMinutes(10).AddSeconds(-5));
        (await store.GetAsync(reg.DeviceId))!.LeaseUntil.Should().Be(result.LeaseUntil);
    }

    [Fact]
    public async Task Heartbeat_unknown_device_returns_null()
    {
        var (svc, _) = Build();
        (await svc.HeartbeatAsync("nope")).Should().BeNull();
    }

    [Fact]
    public async Task Approve_unknown_code_returns_false()
    {
        var (svc, _) = Build();
        (await svc.ApproveAsync("ZZZZZZ")).Should().BeFalse();
    }

    [Fact]
    public async Task CompletePairing_before_approval_returns_not_approved()
    {
        var (svc, _) = Build();
        var start = await svc.StartPairingAsync("Laptop");

        var result = await svc.CompletePairingAsync(start.PairingId);

        result.Approved.Should().BeFalse();
        result.ApiKey.Should().BeNull();
    }

    [Fact]
    public async Task CompletePairing_after_approval_returns_key_once()
    {
        var (svc, _) = Build();
        var start = await svc.StartPairingAsync("Laptop");
        await svc.ApproveAsync(start.Code);

        var first = await svc.CompletePairingAsync(start.PairingId);
        first.Approved.Should().BeTrue();
        first.ApiKey.Should().NotBeNullOrWhiteSpace();
        first.DeviceId.Should().NotBeNullOrWhiteSpace();

        var second = await svc.CompletePairingAsync(start.PairingId);
        second.Approved.Should().BeTrue();
        second.ApiKey.Should().BeNull();
    }

    [Fact]
    public async Task CompletePairing_unknown_pairing_returns_not_approved_without_throw()
    {
        var (svc, _) = Build();
        var result = await svc.CompletePairingAsync("does-not-exist");
        result.Approved.Should().BeFalse();
        result.ApiKey.Should().BeNull();
    }
}
