using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace SyncMaster.Server.Tests.Devices;

public class DeviceServiceTests
{
    private static (DeviceService svc, InMemoryDeviceStore store) Build()
    {
        var store = new InMemoryDeviceStore();
        return (new DeviceService(store), store);
    }

    [Fact]
    public void Ctor_null_store_throws()
    {
        Action act = () => new DeviceService(null!);
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
        ApiKeyHasher.Verify(pending.OneTimeApiKey!, device.ApiKeyHash).Should().BeTrue();
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
