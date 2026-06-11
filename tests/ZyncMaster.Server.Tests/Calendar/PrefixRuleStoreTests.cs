using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Server;
using ZyncMaster.Server.Data;
using ZyncMaster.Server.Tests.Storage;
using Xunit;

namespace ZyncMaster.Server.Tests.Calendar;

public class PrefixRuleStoreTests
{
    private sealed class FixedCurrentUser : ICurrentUserAccessor
    {
        public FixedCurrentUser(string userId) => UserId = userId;
        public string UserId { get; }
    }

    private static EfPrefixRuleStore BuildStore(EfStoreTestHarness h, string userId) =>
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

    private static PrefixRule Rule(string id = "rule-1", int sortOrder = 0) => new()
    {
        Id = id,
        Prefix = "Lunch",
        MaskTitle = "Lunch",
        Enabled = true,
        SortOrder = sortOrder,
        Destinations = new[]
        {
            new PrefixRuleDestination("acc-a", "cal-a"),
            new PrefixRuleDestination("acc-b", "cal-b"),
        },
    };

    [Fact]
    public async Task Add_then_Get_round_trips_the_rule_and_its_destinations()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        var store = BuildStore(h, "user-1");

        await store.AddAsync(Rule());

        var fetched = await store.GetAsync("rule-1");
        fetched.Should().NotBeNull();
        fetched!.UserId.Should().Be("user-1");
        fetched.Prefix.Should().Be("Lunch");
        fetched.MaskTitle.Should().Be("Lunch");
        fetched.Enabled.Should().BeTrue();
        fetched.Destinations.Should().BeEquivalentTo(new[]
        {
            new PrefixRuleDestination("acc-a", "cal-a"),
            new PrefixRuleDestination("acc-b", "cal-b"),
        });
    }

    [Fact]
    public async Task List_orders_by_SortOrder_the_first_rule_wins_collisions()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        var store = BuildStore(h, "user-1");
        await store.AddAsync(Rule("rule-b", sortOrder: 2));
        await store.AddAsync(Rule("rule-a", sortOrder: 1));

        var list = await store.ListAsync();

        list.Select(r => r.Id).Should().ContainInOrder("rule-a", "rule-b");
    }

    [Fact]
    public async Task Update_replaces_fields_and_the_whole_destination_list()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        var store = BuildStore(h, "user-1");
        var added = await store.AddAsync(Rule());

        var ok = await store.UpdateAsync(added with
        {
            Prefix = "Gym",
            MaskTitle = "Workout",
            Enabled = false,
            SortOrder = 9,
            Destinations = new[] { new PrefixRuleDestination("acc-c", "cal-c") },
        });

        ok.Should().BeTrue();
        var fetched = await store.GetAsync("rule-1");
        fetched!.Prefix.Should().Be("Gym");
        fetched.MaskTitle.Should().Be("Workout");
        fetched.Enabled.Should().BeFalse();
        fetched.SortOrder.Should().Be(9);
        fetched.Destinations.Should().BeEquivalentTo(
            new[] { new PrefixRuleDestination("acc-c", "cal-c") },
            "the destination list is REPLACED, not merged — membership IS the two-way flag");
    }

    [Fact]
    public async Task Remove_deletes_the_rule_and_its_destinations()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        var store = BuildStore(h, "user-1");
        await store.AddAsync(Rule());

        (await store.RemoveAsync("rule-1")).Should().BeTrue();

        (await store.GetAsync("rule-1")).Should().BeNull();
        using var db = h.NewContext();
        db.PrefixRuleDestinations.Count().Should().Be(0, "destination rows must not be orphaned");
    }

    [Fact]
    public async Task CrossUser_get_update_and_remove_never_leak_or_mutate()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        SeedUser(h, "user-2");
        var owner = BuildStore(h, "user-1");
        var intruder = BuildStore(h, "user-2");
        var added = await owner.AddAsync(Rule());

        (await intruder.GetAsync("rule-1")).Should().BeNull();
        (await intruder.ListAsync()).Should().BeEmpty();
        (await intruder.UpdateAsync(added with { Enabled = false })).Should().BeFalse();
        (await intruder.RemoveAsync("rule-1")).Should().BeFalse();

        (await owner.GetAsync("rule-1"))!.Enabled.Should().BeTrue();
    }
}
