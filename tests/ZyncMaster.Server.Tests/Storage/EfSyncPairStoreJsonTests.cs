using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server.Tests.Storage;

// Edge cases of the SyncPair endpoint-JSON column round-trip that the happy-path
// EfSyncPairStoreTests do not cover: null AccountRef must stay null (the camelCase serializer
// uses NullValueHandling.Ignore, so the property is omitted and must deserialize back to null,
// NOT to "" or "default"); a null LastResult must persist as no column; and a populated
// MirrorResult with a Failures list must survive the round-trip intact.
public class EfSyncPairStoreJsonTests
{
    private static SyncPair PairWithNullRefs(string id = "p-null") => new()
    {
        Id = id,
        Name = "Null refs",
        Source = new Endpoint { Provider = "OutlookCom", AccountRef = null, CalendarId = "src", CalendarName = "" },
        Destination = new Endpoint { Provider = "MicrosoftGraph", AccountRef = null, CalendarId = "dst", CalendarName = "Dst" },
        IntervalMin = 15,
        State = "active",
    };

    [Fact]
    public async Task Null_account_refs_round_trip_as_null_not_empty_or_default()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfSyncPairStore(h.Factory, h.CurrentUser);

        await store.AddAsync(PairWithNullRefs());
        var fetched = await store.GetAsync("p-null");

        fetched!.Source.AccountRef.Should().BeNull();
        fetched.Destination.AccountRef.Should().BeNull();
        // CalendarName default ("") must also survive rather than vanishing.
        fetched.Source.CalendarName.Should().BeEmpty();
        fetched.Destination.CalendarName.Should().Be("Dst");
    }

    [Fact]
    public async Task Null_last_result_round_trips_as_null()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfSyncPairStore(h.Factory, h.CurrentUser);

        await store.AddAsync(PairWithNullRefs("p-noresult"));
        var fetched = await store.GetAsync("p-noresult");

        fetched!.LastResult.Should().BeNull();
        fetched.LastRunUtc.Should().BeNull();

        // The persisted column is genuinely null (not the string "null").
        await using var db = h.NewContext();
        var row = await db.SyncPairs.AsNoTracking().SingleAsync(p => p.Id == "p-noresult");
        row.LastResultJson.Should().BeNull();
    }

    [Fact]
    public async Task Last_result_with_failures_list_round_trips_intact()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfSyncPairStore(h.Factory, h.CurrentUser);
        await store.AddAsync(PairWithNullRefs("p-result"));

        await store.UpdateAsync(PairWithNullRefs("p-result") with
        {
            LastRunUtc = DateTimeOffset.UtcNow,
            LastResult = new MirrorResult
            {
                Created = 4,
                Updated = 2,
                Deleted = 1,
                Skipped = 3,
                Failures = new List<string> { "evt-7: throttled", "evt-9: bad time zone" },
            },
        });

        var fetched = await store.GetAsync("p-result");
        var result = fetched!.LastResult!;
        result.Created.Should().Be(4);
        result.Updated.Should().Be(2);
        result.Deleted.Should().Be(1);
        result.Skipped.Should().Be(3);
        result.Failures.Should().BeEquivalentTo(new[] { "evt-7: throttled", "evt-9: bad time zone" });
    }

    [Fact]
    public async Task Update_nonexistent_pair_is_a_silent_noop()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfSyncPairStore(h.Factory, h.CurrentUser);

        // Updating an id that was never added must not throw and must not insert a row.
        await store.Invoking(s => s.UpdateAsync(PairWithNullRefs("ghost")))
            .Should().NotThrowAsync();

        (await store.GetAsync("ghost")).Should().BeNull();
        (await store.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Remove_nonexistent_pair_is_a_silent_noop()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfSyncPairStore(h.Factory, h.CurrentUser);

        await store.Invoking(s => s.RemoveAsync("ghost")).Should().NotThrowAsync();
    }

    [Fact]
    public async Task ListBy_account_queries_return_empty_when_no_pairs_match()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfSyncPairStore(h.Factory, h.CurrentUser);
        await store.AddAsync(PairWithNullRefs("p1") with
        {
            Destination = new Endpoint { Provider = "MicrosoftGraph", AccountRef = "alice@test", CalendarId = "d" },
        });

        (await store.ListByDestinationAccountAsync("nobody@test")).Should().BeEmpty();
        (await store.ListBySourceAccountAsync("nobody@test")).Should().BeEmpty();
    }
}
