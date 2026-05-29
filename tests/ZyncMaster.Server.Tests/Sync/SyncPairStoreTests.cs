using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

public class SyncPairStoreTests
{
    private static SyncPair MakePair(
        string id,
        string? destAccount = "default",
        string? srcAccount = null,
        string state = "active") =>
        new()
        {
            Id = id,
            Name = "Pair " + id,
            Source = new Endpoint
            {
                Provider = "OutlookCom",
                AccountRef = srcAccount,
                CalendarId = "src-cal",
            },
            Destination = new Endpoint
            {
                Provider = "MicrosoftGraph",
                AccountRef = destAccount,
                CalendarId = "dst-cal",
            },
            IntervalMin = 15,
            State = state,
        };

    [Fact]
    public async Task Add_then_Get_round_trips()
    {
        var store = new InMemorySyncPairStore();
        var pair = MakePair("p1");

        await store.AddAsync(pair);

        (await store.GetAsync("p1")).Should().BeEquivalentTo(pair);
    }

    [Fact]
    public async Task Get_unknown_id_returns_null()
    {
        var store = new InMemorySyncPairStore();

        (await store.GetAsync("nope")).Should().BeNull();
    }

    [Fact]
    public async Task List_returns_all_added()
    {
        var store = new InMemorySyncPairStore();
        await store.AddAsync(MakePair("p1"));
        await store.AddAsync(MakePair("p2"));

        (await store.ListAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task Update_replaces_existing()
    {
        var store = new InMemorySyncPairStore();
        await store.AddAsync(MakePair("p1"));

        await store.UpdateAsync(MakePair("p1") with { Name = "Renamed", State = "paused" });

        var updated = await store.GetAsync("p1");
        updated!.Name.Should().Be("Renamed");
        updated.State.Should().Be("paused");
    }

    [Fact]
    public async Task Remove_deletes()
    {
        var store = new InMemorySyncPairStore();
        await store.AddAsync(MakePair("p1"));

        await store.RemoveAsync("p1");

        (await store.GetAsync("p1")).Should().BeNull();
    }

    [Fact]
    public async Task ListByDestinationAccount_matches_explicit_and_default_fallback()
    {
        var store = new InMemorySyncPairStore();
        await store.AddAsync(MakePair("p1", destAccount: "default"));
        await store.AddAsync(MakePair("p2", destAccount: null));     // unset → default
        await store.AddAsync(MakePair("p3", destAccount: "other@test"));

        var matches = await store.ListByDestinationAccountAsync("default");

        matches.Select(p => p.Id).Should().BeEquivalentTo(new[] { "p1", "p2" });
    }

    [Fact]
    public async Task ListBySourceAccount_matches_explicit_ref()
    {
        var store = new InMemorySyncPairStore();
        await store.AddAsync(MakePair("p1", srcAccount: "alice@test"));
        await store.AddAsync(MakePair("p2", srcAccount: "bob@test"));

        var matches = await store.ListBySourceAccountAsync("alice@test");

        matches.Select(p => p.Id).Should().BeEquivalentTo(new[] { "p1" });
    }
}
