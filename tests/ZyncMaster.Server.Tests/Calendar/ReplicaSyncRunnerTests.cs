using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZyncMaster.Graph;
using ZyncMaster.Server;
using ZyncMaster.Server.Data;
using ZyncMaster.Server.Tests.Storage;
using Xunit;
using static ZyncMaster.Server.Tests.Calendar.CalendarV2TestHelper;

namespace ZyncMaster.Server.Tests.Calendar;

public class ReplicaSyncRunnerTests
{
    // One accessor that is BOTH the ambient user and the override seam, mirroring how the
    // production HttpContextCurrentUserAccessor honors IHttpCurrentUserOverride.
    private sealed class SwitchableUser : ICurrentUserAccessor, IHttpCurrentUserOverride
    {
        private string _userId = DefaultCurrentUserAccessor.DefaultUserId;
        public string UserId => _userId;
        public void Set(string userId) => _userId = userId;
        public void Clear() => _userId = DefaultCurrentUserAccessor.DefaultUserId;
    }

    private readonly EfStoreTestHarness _h = new();
    private readonly SwitchableUser _user = new();
    private readonly Dictionary<string, FakeReplicaClient> _clients = new();

    private ReplicaSyncRunner Sut()
    {
        var accounts = new EfCalendarAccountStore(_h.Factory, _user,
            Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create("tests"));
        var links = new EfReplicaLinkStore(_h.Factory, _user);
        var rules = new EfPrefixRuleStore(_h.Factory, _user);
        var service = new ReplicaService(accounts, links, id => _clients[id], TimeProvider.System);
        return new ReplicaSyncRunner(
            _h.Factory, _user, accounts, links, rules,
            id => _clients[id], service, new PrefixRuleEvaluator(service),
            Options.Create(new ServerOptions()), NullLogger<ReplicaSyncRunner>.Instance);
    }

    private void SeedUser(string userId)
    {
        using var db = _h.NewContext();
        db.Users.Add(new UserRow
        {
            Id = userId, Provider = "local", Subject = userId, DisplayName = userId,
            CreatedUtc = DateTimeOffset.UtcNow, PrimaryEmail = $"{userId}@test",
        });
        db.SaveChanges();
    }

    private void SeedAccountRow(string userId, string accountId,
        AccountScope scope = AccountScope.ReadWrite)
    {
        using var db = _h.NewContext();
        db.CalendarAccounts.Add(new CalendarAccountRow
        {
            Id = accountId, UserId = userId, Kind = "Graph", Provider = "microsoft",
            AccountEmail = $"{accountId}@test", Scope = scope.ToString(), Status = "active",
            ConnectedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private void SeedLinkRow(string userId, string id, string status = "active",
        string contentHash = "STALE", string destEventId = "rep-1")
    {
        using var db = _h.NewContext();
        db.ReplicaLinks.Add(new ReplicaLinkRow
        {
            Id = id, UserId = userId, SourceAccountId = "acc-src",
            SourceEventId = "stable-1", SourceGraphEventId = "ev-1", SourceKind = "graph",
            DestinationAccountId = "acc-dst", DestinationCalendarId = "cal-dst",
            DestinationEventId = destEventId, MaskTitle = "Busy", ContentHash = contentHash,
            Status = status, CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    // Tomorrow at 10:00 UTC — inside the runner's [today, +SyncWindowDays] window.
    private static DateTimeOffset T0 => new DateTimeOffset(
        DateTime.UtcNow.Date.AddDays(1).AddHours(10), TimeSpan.Zero);

    private static SourceEventSnapshot SourceEvent() => new()
    {
        GraphEventId = "ev-1", StableId = "stable-1", Subject = "Secret",
        Start = T0, End = T0.AddHours(1), ShowAs = "busy",
    };

    private string LinkStatus(string id)
    {
        using var db = _h.NewContext();
        return db.ReplicaLinks.Find(id)!.Status;
    }

    [Fact]
    public async Task Moved_origin_patches_the_replica_times_and_updates_the_hash()
    {
        SeedUser("u1");
        SeedAccountRow("u1", "acc-src");
        SeedAccountRow("u1", "acc-dst");
        SeedLinkRow("u1", "link-1");
        var src = new FakeReplicaClient();
        var dst = new FakeReplicaClient();
        src.EventsById["ev-1"] = SourceEvent();
        dst.WindowReplicas.Add(new ReplicaEventRef { EventId = "rep-1", SourceEventId = "stable-1" });
        _clients["acc-src"] = src;
        _clients["acc-dst"] = dst;

        var summary = await Sut().RunAsync();

        summary.Moved.Should().Be(1);
        dst.PatchedTimes.Should().ContainSingle(p => p.EventId == "rep-1");
        using var db = _h.NewContext();
        db.ReplicaLinks.Find("link-1")!.ContentHash.Should().NotBe("STALE");
    }

    [Fact]
    public async Task Unchanged_origin_is_skipped_by_hash_zero_graph_writes()
    {
        SeedUser("u1");
        SeedAccountRow("u1", "acc-src");
        SeedAccountRow("u1", "acc-dst");
        var ev = SourceEvent();
        SeedLinkRow("u1", "link-1",
            contentHash: ReplicaContentHash.For(ev.Start, ev.End, ev.ShowAs, ev.IsAllDay));
        var src = new FakeReplicaClient();
        var dst = new FakeReplicaClient();
        src.EventsById["ev-1"] = ev;
        dst.WindowReplicas.Add(new ReplicaEventRef { EventId = "rep-1", SourceEventId = "stable-1" });
        _clients["acc-src"] = src;
        _clients["acc-dst"] = dst;

        var summary = await Sut().RunAsync();

        summary.Moved.Should().Be(0);
        dst.PatchedTimes.Should().BeEmpty("skip-by-hash: an unchanged origin costs no PATCH");
    }

    [Fact]
    public async Task Cancelled_or_deleted_origin_deletes_the_replica_and_tombstones()
    {
        SeedUser("u1");
        SeedAccountRow("u1", "acc-src");
        SeedAccountRow("u1", "acc-dst");
        SeedLinkRow("u1", "link-1");
        var src = new FakeReplicaClient(); // EventsById empty -> GetEventAsync returns null
        var dst = new FakeReplicaClient();
        _clients["acc-src"] = src;
        _clients["acc-dst"] = dst;

        var summary = await Sut().RunAsync();

        summary.Cancelled.Should().Be(1);
        dst.Deleted.Should().ContainSingle(id => id == "rep-1");
        LinkStatus("link-1").Should().Be("tombstone");
    }

    [Fact]
    public async Task Manually_deleted_replica_marks_the_link_broken_never_recreates_silently()
    {
        SeedUser("u1");
        SeedAccountRow("u1", "acc-src");
        SeedAccountRow("u1", "acc-dst");
        SeedLinkRow("u1", "link-1");
        var src = new FakeReplicaClient();
        var dst = new FakeReplicaClient(); // WindowReplicas empty -> the replica is gone
        src.EventsById["ev-1"] = SourceEvent();
        _clients["acc-src"] = src;
        _clients["acc-dst"] = dst;

        var summary = await Sut().RunAsync();

        summary.Broken.Should().Be(1);
        LinkStatus("link-1").Should().Be("broken",
            "broken is a USER decision point (recreate/discard/write-back) — the runner never " +
            "resolves it on its own");
        dst.CreatedReplicas.Should().BeEmpty();
    }

    [Fact]
    public async Task Prefix_rules_run_only_on_readwrite_accounts_and_apply_strip_fanout()
    {
        SeedUser("u1");
        SeedAccountRow("u1", "acc-rw");
        SeedAccountRow("u1", "acc-ro", AccountScope.Read);
        using (var db = _h.NewContext())
        {
            db.PrefixRules.Add(new PrefixRuleRow
            {
                Id = "rule-1", UserId = "u1", Prefix = "Lunch", MaskTitle = "Lunch",
                Enabled = true, SortOrder = 0,
            });
            db.PrefixRuleDestinations.Add(new PrefixRuleDestinationRow
            {
                Id = "d1", RuleId = "rule-1", AccountId = "acc-rw", CalendarId = "cal-2",
            });
            db.SaveChanges();
        }
        var rw = new FakeReplicaClient();
        var ro = new FakeReplicaClient();
        rw.Calendars.Add(new CalendarTargetInfo { Id = "cal-1", DisplayName = "Main" });
        rw.WindowEvents.Add(SourceEvent() with { Subject = "[Lunch] Pizza" });
        ro.Calendars.Add(new CalendarTargetInfo { Id = "cal-9", DisplayName = "ReadOnly" });
        ro.WindowEvents.Add(SourceEvent() with { GraphEventId = "ev-9", Subject = "[Lunch] Sushi" });
        _clients["acc-rw"] = rw;
        _clients["acc-ro"] = ro;

        var summary = await Sut().RunAsync();

        summary.RulesApplied.Should().Be(1);
        rw.PatchedSubjects.Should().ContainSingle(p => p.Subject == "Pizza");
        rw.Stamps.Should().ContainSingle(s => s.RuleId == "rule-1");
        ro.PatchedSubjects.Should().BeEmpty(
            "a read account is never half-evaluated: the rename and the stamp are writes (spec §5)");
    }

    [Fact]
    public async Task Each_user_is_processed_under_its_own_identity_and_isolated_from_failures()
    {
        SeedUser("u1");
        SeedUser("u2");
        SeedAccountRow("u1", "acc-src");
        SeedAccountRow("u1", "acc-dst");
        SeedLinkRow("u1", "link-1");
        // u2 has a link whose client factory blows up -> that user fails, u1 still processes.
        using (var db = _h.NewContext())
        {
            db.CalendarAccounts.Add(new CalendarAccountRow
            {
                Id = "acc-broken", UserId = "u2", Kind = "Graph", Provider = "microsoft",
                AccountEmail = "x@test", Scope = "ReadWrite", Status = "active",
                ConnectedAt = DateTimeOffset.UtcNow,
            });
            db.ReplicaLinks.Add(new ReplicaLinkRow
            {
                Id = "link-2", UserId = "u2", SourceAccountId = "acc-broken",
                SourceEventId = "s2", SourceGraphEventId = "e2", SourceKind = "graph",
                DestinationAccountId = "acc-broken", DestinationCalendarId = "cal",
                DestinationEventId = "r2", MaskTitle = "Busy", ContentHash = "X",
                Status = "active", CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }
        var src = new FakeReplicaClient();
        var dst = new FakeReplicaClient();
        src.EventsById["ev-1"] = SourceEvent();
        dst.WindowReplicas.Add(new ReplicaEventRef { EventId = "rep-1", SourceEventId = "stable-1" });
        _clients["acc-src"] = src;
        _clients["acc-dst"] = dst;
        // "acc-broken" deliberately NOT registered -> KeyNotFoundException for u2.

        var summary = await Sut().RunAsync();

        summary.Moved.Should().Be(1, "u1's link must be reconciled even though u2 failed");
        summary.Failed.Should().BeGreaterThan(0);
        LinkStatus("link-1").Should().Be("active");
    }

    [Fact]
    public async Task Com_links_are_skipped_in_v1_reserved_for_the_snapshot_work()
    {
        SeedUser("u1");
        SeedAccountRow("u1", "acc-dst");
        using (var db = _h.NewContext())
        {
            db.ReplicaLinks.Add(new ReplicaLinkRow
            {
                Id = "link-com", UserId = "u1", SourceAccountId = null,
                SourceEventId = "s", SourceGraphEventId = "", SourceKind = "com",
                DestinationAccountId = "acc-dst", DestinationCalendarId = "cal-dst",
                DestinationEventId = "r", MaskTitle = "Busy", ContentHash = "X",
                Status = "active", CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }
        _clients["acc-dst"] = new FakeReplicaClient();

        var summary = await Sut().RunAsync();

        summary.Failed.Should().Be(0);
        LinkStatus("link-com").Should().Be("active", "com links are untouched until v1.1");
    }
}
