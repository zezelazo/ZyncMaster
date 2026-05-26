using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace SyncMaster.Server.Tests.Storage;

public class InMemoryDeviceStoreTests
{
    private static Device SampleDevice(string id = "dev-1", string name = "Phone") => new()
    {
        Id = id,
        Name = name,
        ApiKeyHash = "hash-" + id,
        CreatedUtc = DateTimeOffset.UtcNow,
    };

    private static PendingPairing SamplePending(string id = "pair-1", string code = "ABC123") => new()
    {
        PairingId = id,
        DeviceName = "Tablet",
        Code = code,
        CreatedUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Add_then_get_returns_stored_device()
    {
        var store = new InMemoryDeviceStore();
        var device = SampleDevice();

        var added = await store.AddAsync(device);
        added.Should().BeEquivalentTo(device);

        var fetched = await store.GetAsync(device.Id);
        fetched.Should().BeEquivalentTo(device);
    }

    [Fact]
    public async Task List_returns_all_added_devices()
    {
        var store = new InMemoryDeviceStore();
        await store.AddAsync(SampleDevice("dev-1", "A"));
        await store.AddAsync(SampleDevice("dev-2", "B"));

        var list = await store.ListAsync();

        list.Should().HaveCount(2);
        list.Select(d => d.Id).Should().BeEquivalentTo(new[] { "dev-1", "dev-2" });
    }

    [Fact]
    public async Task Get_unknown_device_returns_null()
    {
        var store = new InMemoryDeviceStore();

        var fetched = await store.GetAsync("nope");

        fetched.Should().BeNull();
    }

    [Fact]
    public async Task Remove_deletes_device()
    {
        var store = new InMemoryDeviceStore();
        await store.AddAsync(SampleDevice());

        await store.RemoveAsync("dev-1");

        (await store.GetAsync("dev-1")).Should().BeNull();
        (await store.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Update_persists_changes()
    {
        var store = new InMemoryDeviceStore();
        await store.AddAsync(SampleDevice());

        var updated = SampleDevice() with { TargetCalendarId = "cal-99", Name = "Renamed" };
        await store.UpdateAsync(updated);

        var fetched = await store.GetAsync("dev-1");
        fetched.Should().NotBeNull();
        fetched!.TargetCalendarId.Should().Be("cal-99");
        fetched.Name.Should().Be("Renamed");
    }

    [Fact]
    public async Task Save_pending_then_get_by_id_round_trip()
    {
        var store = new InMemoryDeviceStore();
        var pending = SamplePending();

        await store.SavePendingAsync(pending);

        var fetched = await store.GetPendingAsync(pending.PairingId);
        fetched.Should().BeEquivalentTo(pending);
    }

    [Fact]
    public async Task Get_pending_by_code_returns_match()
    {
        var store = new InMemoryDeviceStore();
        await store.SavePendingAsync(SamplePending("pair-1", "CODE-A"));
        await store.SavePendingAsync(SamplePending("pair-2", "CODE-B"));

        var fetched = await store.GetPendingByCodeAsync("CODE-B");

        fetched.Should().NotBeNull();
        fetched!.PairingId.Should().Be("pair-2");
    }

    [Fact]
    public async Task Get_pending_by_unknown_code_returns_null()
    {
        var store = new InMemoryDeviceStore();
        await store.SavePendingAsync(SamplePending());

        var fetched = await store.GetPendingByCodeAsync("MISSING");

        fetched.Should().BeNull();
    }

    [Fact]
    public async Task Get_pending_unknown_id_returns_null()
    {
        var store = new InMemoryDeviceStore();

        (await store.GetPendingAsync("nope")).Should().BeNull();
    }

    [Fact]
    public async Task Update_pending_persists_changes()
    {
        var store = new InMemoryDeviceStore();
        await store.SavePendingAsync(SamplePending());

        var approved = SamplePending() with
        {
            Approved = true,
            ApprovedDeviceId = "dev-7",
            OneTimeApiKey = "secret",
        };
        await store.UpdatePendingAsync(approved);

        var fetched = await store.GetPendingAsync("pair-1");
        fetched.Should().NotBeNull();
        fetched!.Approved.Should().BeTrue();
        fetched.ApprovedDeviceId.Should().Be("dev-7");
        fetched.OneTimeApiKey.Should().Be("secret");
    }

    [Fact]
    public async Task Remove_pending_deletes_entry()
    {
        var store = new InMemoryDeviceStore();
        await store.SavePendingAsync(SamplePending());

        await store.RemovePendingAsync("pair-1");

        (await store.GetPendingAsync("pair-1")).Should().BeNull();
        (await store.GetPendingByCodeAsync("ABC123")).Should().BeNull();
    }
}

public class InMemorySyncStateStoreTests
{
    [Fact]
    public async Task Set_then_get_round_trip()
    {
        var store = new InMemorySyncStateStore();
        var state = new SyncState
        {
            DeviceId = "dev-1",
            LastSyncUtc = DateTimeOffset.UtcNow,
            LastCreated = 3,
            LastUpdated = 2,
            LastDeleted = 1,
        };

        await store.SetAsync(state);

        var fetched = await store.GetAsync("dev-1");
        fetched.Should().BeEquivalentTo(state);
    }

    [Fact]
    public async Task Set_overwrites_existing_state()
    {
        var store = new InMemorySyncStateStore();
        await store.SetAsync(new SyncState { DeviceId = "dev-1", LastCreated = 1 });
        await store.SetAsync(new SyncState { DeviceId = "dev-1", LastCreated = 9 });

        var fetched = await store.GetAsync("dev-1");
        fetched.Should().NotBeNull();
        fetched!.LastCreated.Should().Be(9);
    }

    [Fact]
    public async Task Get_unknown_device_returns_null()
    {
        var store = new InMemorySyncStateStore();

        (await store.GetAsync("nope")).Should().BeNull();
    }
}
