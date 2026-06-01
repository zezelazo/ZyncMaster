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
        ICurrentUserAccessor? user = null, ServerOptions? options = null)
    {
        var store = new InMemoryDeviceStore();
        var svc = new DeviceService(
            store,
            user ?? new DefaultCurrentUserAccessor(),
            Options.Create(options ?? new ServerOptions()));
        return (svc, store);
    }

    private sealed class FixedUser : ICurrentUserAccessor
    {
        public FixedUser(string userId) => UserId = userId;
        public string UserId { get; }
    }

    [Fact]
    public void Ctor_null_store_throws()
    {
        Action act = () => new DeviceService(null!, new DefaultCurrentUserAccessor(), Options.Create(new ServerOptions()));
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
