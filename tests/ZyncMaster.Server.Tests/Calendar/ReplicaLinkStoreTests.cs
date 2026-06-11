using System;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Server;
using ZyncMaster.Server.Data;
using ZyncMaster.Server.Tests.Storage;
using Xunit;

namespace ZyncMaster.Server.Tests.Calendar;

public class ReplicaLinkStoreTests
{
    private sealed class FixedCurrentUser : ICurrentUserAccessor
    {
        public FixedCurrentUser(string userId) => UserId = userId;
        public string UserId { get; }
    }

    private static EfReplicaLinkStore BuildStore(EfStoreTestHarness h, string userId) =>
        new(h.Factory, new FixedCurrentUser(userId));

    private static void SeedUser(EfStoreTestHarness h, string userId)
    {
        using var db = h.NewContext();
        db.Users.Add(new UserRow
        {
            Id = userId,
            Provider = "local",
            Subject = userId,
            DisplayName = userId,
            CreatedUtc = DateTimeOffset.UtcNow,
            PrimaryEmail = $"{userId}@test",
        });
        db.SaveChanges();
    }

    private static ReplicaLink Link(string id = "link-1") => new()
    {
        Id = id,
        SourceAccountId = "acc-src",
        SourceEventId = "9f0c2a51-1111-2222-3333-444455556666",
        SourceGraphEventId = "graph-ev-1",
        SourceKind = "graph",
        DestinationAccountId = "acc-dst",
        DestinationCalendarId = "cal-dst",
        DestinationEventId = "dst-ev-1",
        MaskTitle = "Busy",
        ContentHash = "AAA",
        CreatedUtc = DateTimeOffset.UtcNow,
        UpdatedUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Add_stamps_the_ambient_user_and_Get_round_trips_every_field()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        var store = BuildStore(h, "user-1");

        await store.AddAsync(Link());

        var fetched = await store.GetAsync("link-1");
        fetched.Should().NotBeNull();
        fetched!.UserId.Should().Be("user-1");
        fetched.SourceAccountId.Should().Be("acc-src");
        fetched.SourceEventId.Should().Be("9f0c2a51-1111-2222-3333-444455556666");
        fetched.SourceGraphEventId.Should().Be("graph-ev-1");
        fetched.SourceKind.Should().Be("graph");
        fetched.DestinationAccountId.Should().Be("acc-dst");
        fetched.DestinationCalendarId.Should().Be("cal-dst");
        fetched.DestinationEventId.Should().Be("dst-ev-1");
        fetched.MaskTitle.Should().Be("Busy");
        fetched.RuleId.Should().BeNull();
        fetched.ContentHash.Should().Be("AAA");
        fetched.Status.Should().Be(ReplicaLinkStatus.Active);
    }

    [Fact]
    public async Task ListBySourceEvent_returns_only_that_source_links()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        var store = BuildStore(h, "user-1");
        await store.AddAsync(Link("l1"));
        await store.AddAsync(Link("l2") with { SourceEventId = "other-source" });

        var links = await store.ListBySourceEventAsync("9f0c2a51-1111-2222-3333-444455556666");

        links.Should().ContainSingle(l => l.Id == "l1");
    }

    [Fact]
    public async Task Update_persists_status_hash_title_and_destination_event()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        var store = BuildStore(h, "user-1");
        var added = await store.AddAsync(Link());

        var ok = await store.UpdateAsync(added with
        {
            Status = ReplicaLinkStatus.Broken,
            ContentHash = "BBB",
            MaskTitle = "Focus",
            DestinationEventId = "dst-ev-2",
            UpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(1),
        });

        ok.Should().BeTrue();
        var fetched = await store.GetAsync("link-1");
        fetched!.Status.Should().Be(ReplicaLinkStatus.Broken);
        fetched.ContentHash.Should().Be("BBB");
        fetched.MaskTitle.Should().Be("Focus");
        fetched.DestinationEventId.Should().Be("dst-ev-2");
    }

    [Fact]
    public async Task CrossUser_get_list_and_update_never_leak_or_mutate()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        SeedUser(h, "user-2");
        var owner = BuildStore(h, "user-1");
        var intruder = BuildStore(h, "user-2");
        var added = await owner.AddAsync(Link());

        (await intruder.GetAsync("link-1")).Should().BeNull();
        (await intruder.ListAsync()).Should().BeEmpty();
        (await intruder.ListBySourceEventAsync(added.SourceEventId)).Should().BeEmpty();
        (await intruder.UpdateAsync(added with { Status = ReplicaLinkStatus.Tombstone }))
            .Should().BeFalse("a cross-user update must be a no-op");

        (await owner.GetAsync("link-1"))!.Status.Should().Be(ReplicaLinkStatus.Active);
    }

    [Fact]
    public async Task Get_unknown_id_returns_null_and_update_unknown_returns_false()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        var store = BuildStore(h, "user-1");

        (await store.GetAsync("missing")).Should().BeNull();
        (await store.UpdateAsync(Link("missing"))).Should().BeFalse();
    }
}
