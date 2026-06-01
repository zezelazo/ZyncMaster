using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ZyncMaster.Server.Tests.Storage;

public class EfDeviceStoreTests
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
    public async Task Add_then_get_round_trips()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfDeviceStore(h.Factory, h.CurrentUser);
        var device = SampleDevice();

        await store.AddAsync(device);

        (await store.GetAsync("dev-1")).Should().BeEquivalentTo(device);
    }

    [Fact]
    public async Task List_returns_all_for_current_user()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfDeviceStore(h.Factory, h.CurrentUser);
        await store.AddAsync(SampleDevice("dev-1", "A"));
        await store.AddAsync(SampleDevice("dev-2", "B"));

        var list = await store.ListAsync();

        list.Select(d => d.Id).Should().BeEquivalentTo(new[] { "dev-1", "dev-2" });
    }

    [Fact]
    public async Task Get_unknown_returns_null()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfDeviceStore(h.Factory, h.CurrentUser);

        (await store.GetAsync("nope")).Should().BeNull();
    }

    [Fact]
    public async Task Update_persists_changes()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfDeviceStore(h.Factory, h.CurrentUser);
        await store.AddAsync(SampleDevice());

        await store.UpdateAsync(SampleDevice() with { TargetCalendarId = "cal-99", Name = "Renamed" });

        var fetched = await store.GetAsync("dev-1");
        fetched!.TargetCalendarId.Should().Be("cal-99");
        fetched.Name.Should().Be("Renamed");
    }

    [Fact]
    public async Task Remove_deletes()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfDeviceStore(h.Factory, h.CurrentUser);
        await store.AddAsync(SampleDevice());

        await store.RemoveAsync("dev-1");

        (await store.GetAsync("dev-1")).Should().BeNull();
        (await store.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Lease_and_capability_fields_round_trip()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfDeviceStore(h.Factory, h.CurrentUser);
        var lease = DateTimeOffset.UtcNow.AddMinutes(10);
        var device = SampleDevice() with
        {
            KeyId = "kid-1",
            Platform = "macos",
            HasOutlookCom = true,
            AppVersion = "2.0.0",
            LeaseUntil = lease,
        };

        await store.AddAsync(device);

        var fetched = await store.GetAsync("dev-1");
        fetched!.KeyId.Should().Be("kid-1");
        fetched.Platform.Should().Be("macos");
        fetched.HasOutlookCom.Should().BeTrue();
        fetched.AppVersion.Should().Be("2.0.0");
        fetched.LeaseUntil.Should().BeCloseTo(lease, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Pending_round_trip_by_id_and_code()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfDeviceStore(h.Factory, h.CurrentUser);
        await store.SavePendingAsync(SamplePending("pair-1", "CODE-A"));
        await store.SavePendingAsync(SamplePending("pair-2", "CODE-B"));

        (await store.GetPendingAsync("pair-1"))!.Code.Should().Be("CODE-A");
        (await store.GetPendingByCodeAsync("CODE-B"))!.PairingId.Should().Be("pair-2");
        (await store.GetPendingByCodeAsync("MISSING")).Should().BeNull();
    }

    [Fact]
    public async Task Update_pending_then_remove()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfDeviceStore(h.Factory, h.CurrentUser);
        await store.SavePendingAsync(SamplePending());

        await store.UpdatePendingAsync(SamplePending() with
        {
            Approved = true,
            ApprovedDeviceId = "dev-7",
            OneTimeApiKey = "secret",
        });

        var fetched = await store.GetPendingAsync("pair-1");
        fetched!.Approved.Should().BeTrue();
        fetched.ApprovedDeviceId.Should().Be("dev-7");
        fetched.OneTimeApiKey.Should().Be("secret");

        await store.RemovePendingAsync("pair-1");
        (await store.GetPendingAsync("pair-1")).Should().BeNull();
    }

    private sealed class MutableCurrentUser : ICurrentUserAccessor
    {
        public string UserId { get; set; } = DefaultCurrentUserAccessor.DefaultUserId;
    }

    [Fact]
    public async Task Pending_is_global_not_user_scoped()
    {
        // A pending pairing created by the anonymous device (ambient "default" user) must be
        // visible to a DIFFERENT signed-in user at approval time — the row is not scoped to
        // whoever happened to create it. Save with the "default" actor, then read/update/
        // remove with a real signed-in user via the same store.
        using var h = new EfStoreTestHarness();
        var currentUser = new MutableCurrentUser();
        var store = new EfDeviceStore(h.Factory, currentUser);

        currentUser.UserId = DefaultCurrentUserAccessor.DefaultUserId;
        await store.SavePendingAsync(SamplePending("pair-global", "GLOB01"));

        currentUser.UserId = "signed-in-user";
        (await store.GetPendingByCodeAsync("GLOB01"))!.PairingId.Should().Be("pair-global");
        (await store.GetPendingAsync("pair-global"))!.Code.Should().Be("GLOB01");

        await store.UpdatePendingAsync(SamplePending("pair-global", "GLOB01") with { Approved = true });
        (await store.GetPendingAsync("pair-global"))!.Approved.Should().BeTrue();

        await store.RemovePendingAsync("pair-global");
        (await store.GetPendingAsync("pair-global")).Should().BeNull();

        // And the row is persisted without a forced user id.
        await store.SavePendingAsync(SamplePending("pair-noscope", "GLOB02"));
        await using var db = h.NewContext();
        var row = await db.PendingPairings.AsNoTracking().SingleAsync(p => p.PairingId == "pair-noscope");
        row.UserId.Should().BeNull();
    }
}

public class EfSyncStateStoreTests
{
    [Fact]
    public async Task Set_then_get_round_trip()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfSyncStateStore(h.Factory, h.CurrentUser);
        var state = new SyncState
        {
            DeviceId = "dev-1",
            LastSyncUtc = DateTimeOffset.UtcNow,
            LastCreated = 3,
            LastUpdated = 2,
            LastDeleted = 1,
        };

        await store.SetAsync(state);

        (await store.GetAsync("dev-1")).Should().BeEquivalentTo(state);
    }

    [Fact]
    public async Task Set_overwrites_existing()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfSyncStateStore(h.Factory, h.CurrentUser);
        await store.SetAsync(new SyncState { DeviceId = "dev-1", LastCreated = 1 });
        await store.SetAsync(new SyncState { DeviceId = "dev-1", LastCreated = 9 });

        (await store.GetAsync("dev-1"))!.LastCreated.Should().Be(9);
    }

    [Fact]
    public async Task Get_unknown_returns_null()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfSyncStateStore(h.Factory, h.CurrentUser);

        (await store.GetAsync("nope")).Should().BeNull();
    }
}

public class EfSyncPairStoreTests
{
    private static SyncPair MakePair(
        string id,
        string? destAccount = "default",
        string? srcAccount = null,
        string state = "active") => new()
    {
        Id = id,
        Name = "Pair " + id,
        Source = new Endpoint { Provider = "OutlookCom", AccountRef = srcAccount, CalendarId = "src-cal" },
        Destination = new Endpoint { Provider = "MicrosoftGraph", AccountRef = destAccount, CalendarId = "dst-cal" },
        IntervalMin = 15,
        State = state,
    };

    [Fact]
    public async Task Add_then_get_round_trips_endpoints_as_json()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfSyncPairStore(h.Factory, h.CurrentUser);

        await store.AddAsync(MakePair("p1"));

        var fetched = await store.GetAsync("p1");
        fetched.Should().BeEquivalentTo(MakePair("p1"));
        fetched!.Source.CalendarId.Should().Be("src-cal");
        fetched.Destination.AccountRef.Should().Be("default");
    }

    [Fact]
    public async Task Update_persists_state_and_last_result()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfSyncPairStore(h.Factory, h.CurrentUser);
        await store.AddAsync(MakePair("p1"));

        await store.UpdateAsync(MakePair("p1") with
        {
            Name = "Renamed",
            State = "paused",
            LastRunUtc = DateTimeOffset.UtcNow,
            LastResult = new MirrorResult { Created = 5, Updated = 1 },
        });

        var updated = await store.GetAsync("p1");
        updated!.Name.Should().Be("Renamed");
        updated.State.Should().Be("paused");
        updated.LastRunUtc.Should().NotBeNull();
        updated.LastResult!.Created.Should().Be(5);
        updated.LastResult.Updated.Should().Be(1);
    }

    [Fact]
    public async Task List_and_remove()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfSyncPairStore(h.Factory, h.CurrentUser);
        await store.AddAsync(MakePair("p1"));
        await store.AddAsync(MakePair("p2"));

        (await store.ListAsync()).Should().HaveCount(2);

        await store.RemoveAsync("p1");

        (await store.GetAsync("p1")).Should().BeNull();
        (await store.ListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task ListByDestinationAccount_matches_explicit_and_default_fallback()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfSyncPairStore(h.Factory, h.CurrentUser);
        await store.AddAsync(MakePair("p1", destAccount: "default"));
        await store.AddAsync(MakePair("p2", destAccount: null));
        await store.AddAsync(MakePair("p3", destAccount: "other@test"));

        var matches = await store.ListByDestinationAccountAsync("default");

        matches.Select(p => p.Id).Should().BeEquivalentTo(new[] { "p1", "p2" });
    }

    [Fact]
    public async Task ListBySourceAccount_matches_explicit_ref()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfSyncPairStore(h.Factory, h.CurrentUser);
        await store.AddAsync(MakePair("p1", srcAccount: "alice@test"));
        await store.AddAsync(MakePair("p2", srcAccount: "bob@test"));

        var matches = await store.ListBySourceAccountAsync("alice@test");

        matches.Select(p => p.Id).Should().BeEquivalentTo(new[] { "p1" });
    }
}

public class EfConnectedAccountStoreTests
{
    private static EfConnectedAccountStore BuildStore(EfStoreTestHarness h) =>
        new(h.Factory, h.CurrentUser, DataProtectionProvider.Create("tests"));

    [Fact]
    public async Task Set_then_GetRefreshToken_round_trips_plaintext()
    {
        using var h = new EfStoreTestHarness();
        var store = BuildStore(h);

        await store.SetAsync("user@test", "the-refresh-token");

        (await store.GetRefreshTokenAsync("user@test")).Should().Be("the-refresh-token");
    }

    [Fact]
    public async Task Stored_refresh_token_is_encrypted()
    {
        using var h = new EfStoreTestHarness();
        var store = BuildStore(h);

        await store.SetAsync("user@test", "the-refresh-token");
        var account = await store.GetAsync("user@test");

        account!.EncryptedRefreshToken.Should().NotBe("the-refresh-token");
        account.UserPrincipalName.Should().Be("user@test");
    }

    [Fact]
    public async Task GetRefreshToken_unknown_upn_returns_null()
    {
        using var h = new EfStoreTestHarness();
        var store = BuildStore(h);

        (await store.GetRefreshTokenAsync("nobody@test")).Should().BeNull();
    }

    [Fact]
    public async Task Set_overwrites_existing_token()
    {
        using var h = new EfStoreTestHarness();
        var store = BuildStore(h);
        await store.SetAsync("user@test", "first");

        await store.SetAsync("user@test", "second");

        (await store.GetRefreshTokenAsync("user@test")).Should().Be("second");
        (await store.ListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task HasAny_false_then_true()
    {
        using var h = new EfStoreTestHarness();
        var store = BuildStore(h);

        (await store.HasAnyAsync()).Should().BeFalse();
        await store.SetAsync("user@test", "rt");
        (await store.HasAnyAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Null_or_empty_upn_uses_default_key_round_trip()
    {
        using var h = new EfStoreTestHarness();
        var store = BuildStore(h);

        await store.SetAsync("", "rt-default");

        (await store.GetRefreshTokenAsync("")).Should().Be("rt-default");
        (await store.GetRefreshTokenAsync("default")).Should().Be("rt-default");
    }

    [Fact]
    public async Task List_returns_all_connected_accounts()
    {
        using var h = new EfStoreTestHarness();
        var store = BuildStore(h);
        await store.SetAsync("alice@test", "rt-a");
        await store.SetAsync("bob@test", "rt-b");

        var accounts = await store.ListAsync();

        accounts.Select(a => a.UserPrincipalName).Should().BeEquivalentTo(new[] { "alice@test", "bob@test" });
    }

    [Fact]
    public async Task Remove_deletes_account()
    {
        using var h = new EfStoreTestHarness();
        var store = BuildStore(h);
        await store.SetAsync("alice@test", "rt-a");

        await store.RemoveAsync("alice@test");

        (await store.GetAsync("alice@test")).Should().BeNull();
        (await store.HasAnyAsync()).Should().BeFalse();
    }
}
