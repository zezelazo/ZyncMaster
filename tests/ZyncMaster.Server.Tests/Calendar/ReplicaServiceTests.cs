using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.Server.Tests.Calendar;

public class ReplicaServiceTests
{
    private readonly Mock<ICalendarAccountStore> _accounts = new(MockBehavior.Strict);
    private readonly Mock<IReplicaLinkStore> _links = new(MockBehavior.Strict);
    private readonly Mock<IReplicaGraphClient> _sourceClient = new();
    private readonly Mock<IReplicaGraphClient> _destClient = new();
    private readonly Dictionary<string, IReplicaGraphClient> _clientsByAccount = new();

    private ReplicaService Sut() => new(
        _accounts.Object, _links.Object,
        accountId => _clientsByAccount[accountId],
        TimeProvider.System);

    private static CalendarAccount Account(
        string id, AccountScope scope = AccountScope.ReadWrite, AccountKind kind = AccountKind.Graph) => new()
    {
        Id = id,
        UserId = "user-1",
        Kind = kind,
        Provider = "microsoft",
        AccountEmail = $"{id}@test",
        Scope = scope,
        ConnectedAt = DateTimeOffset.UtcNow,
    };

    private static SourceEventSnapshot Snapshot() => new()
    {
        GraphEventId = "graph-ev-1",
        StableId = "stable-1",
        Subject = "Secret subject",
        Start = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero),
        End = new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero),
        ShowAs = "busy",
    };

    private void SetupHappyPath(SourceEventSnapshot snapshot)
    {
        _clientsByAccount["acc-src"] = _sourceClient.Object;
        _clientsByAccount["acc-dst"] = _destClient.Object;
        _accounts.Setup(a => a.GetAsync("acc-src", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Account("acc-src", AccountScope.Read));
        _accounts.Setup(a => a.GetAsync("acc-dst", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Account("acc-dst"));
        _sourceClient.Setup(c => c.GetEventAsync("graph-ev-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        _links.Setup(l => l.ListBySourceEventAsync("stable-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReplicaLink>());
        _links.Setup(l => l.AddAsync(It.IsAny<ReplicaLink>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReplicaLink l, CancellationToken _) => l with { UserId = "user-1" });
        _destClient.Setup(c => c.CreateReplicaAsync("cal-dst", It.IsAny<ReplicaDraft>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("dst-ev-1");
    }

    private static List<ReplicaDestinationRequest> Destinations(string title = "Busy") =>
        new() { new ReplicaDestinationRequest("acc-dst", "cal-dst", title) };

    [Fact]
    public async Task FanOut_creates_the_replica_and_the_link_with_hash_and_manual_title()
    {
        SetupHappyPath(Snapshot());
        ReplicaDraft? sent = null;
        _destClient.Setup(c => c.CreateReplicaAsync("cal-dst", It.IsAny<ReplicaDraft>(), It.IsAny<CancellationToken>()))
            .Callback((string _, ReplicaDraft d, CancellationToken _) => sent = d)
            .ReturnsAsync("dst-ev-1");

        var outcome = await Sut().FanOutAsync("acc-src", "graph-ev-1", Destinations(), ruleId: null);

        outcome.ErrorCode.Should().BeNull();
        outcome.Created.Should().ContainSingle();
        var link = outcome.Created[0];
        link.SourceEventId.Should().Be("stable-1");
        link.SourceGraphEventId.Should().Be("graph-ev-1");
        link.DestinationEventId.Should().Be("dst-ev-1");
        link.MaskTitle.Should().Be("Busy");
        link.ContentHash.Should().NotBeEmpty();
        link.Status.Should().Be(ReplicaLinkStatus.Active);
        sent!.MaskTitle.Should().Be("Busy");
        sent.MaskTitle.Should().NotContain("Secret",
            "PRIVACY: the draft sent to the destination must carry the manual title only");
    }

    [Fact]
    public async Task FanOut_rejects_a_source_with_the_replica_mark_anti_loop()
    {
        SetupHappyPath(Snapshot() with { HasReplicaMark = true });

        var outcome = await Sut().FanOutAsync("acc-src", "graph-ev-1", Destinations(), null);

        outcome.ErrorCode.Should().Be("replica_cannot_be_source");
        _destClient.Verify(c => c.CreateReplicaAsync(It.IsAny<string>(), It.IsAny<ReplicaDraft>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FanOut_rejects_a_source_with_the_calimport_mark_anti_loop()
    {
        SetupHappyPath(Snapshot() with { HasCalImportMark = true });

        var outcome = await Sut().FanOutAsync("acc-src", "graph-ev-1", Destinations(), null);

        outcome.ErrorCode.Should().Be("replica_cannot_be_source",
            "the pair mirror's events are never replication sources either");
    }

    [Fact]
    public async Task FanOut_rejects_a_cancelled_source()
    {
        SetupHappyPath(Snapshot() with { IsCancelled = true });

        var outcome = await Sut().FanOutAsync("acc-src", "graph-ev-1", Destinations(), null);

        outcome.ErrorCode.Should().Be("source_event_cancelled");
    }

    [Fact]
    public async Task FanOut_rejects_a_readonly_destination_before_creating_anything()
    {
        SetupHappyPath(Snapshot());
        _accounts.Setup(a => a.GetAsync("acc-dst", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Account("acc-dst", AccountScope.Read));

        var outcome = await Sut().FanOutAsync("acc-src", "graph-ev-1", Destinations(), null);

        outcome.ErrorCode.Should().Be("readwrite_scope_required");
        _destClient.Verify(c => c.CreateReplicaAsync(It.IsAny<string>(), It.IsAny<ReplicaDraft>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FanOut_requires_a_non_empty_mask_title()
    {
        SetupHappyPath(Snapshot());

        var outcome = await Sut().FanOutAsync("acc-src", "graph-ev-1", Destinations("  "), null);

        outcome.ErrorCode.Should().Be("mask_title_required",
            "an empty title must never silently default to the source subject");
    }

    [Fact]
    public async Task FanOut_is_idempotent_an_existing_active_link_is_not_duplicated()
    {
        SetupHappyPath(Snapshot());
        _links.Setup(l => l.ListBySourceEventAsync("stable-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReplicaLink>
            {
                new()
                {
                    Id = "existing", SourceEventId = "stable-1",
                    DestinationAccountId = "acc-dst", DestinationCalendarId = "cal-dst",
                    DestinationEventId = "dst-ev-0", MaskTitle = "Busy",
                },
            });

        var outcome = await Sut().FanOutAsync("acc-src", "graph-ev-1", Destinations(), null);

        outcome.ErrorCode.Should().BeNull();
        outcome.Created.Should().BeEmpty("the destination already has an active replica");
        _destClient.Verify(c => c.CreateReplicaAsync(It.IsAny<string>(), It.IsAny<ReplicaDraft>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FanOut_rejects_a_com_source_with_an_explicit_deferral()
    {
        _clientsByAccount["acc-com"] = _sourceClient.Object;
        _accounts.Setup(a => a.GetAsync("acc-com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Account("acc-com", kind: AccountKind.OutlookCom));

        var outcome = await Sut().FanOutAsync("acc-com", "any", Destinations(), null);

        outcome.ErrorCode.Should().Be("com_source_not_supported");
    }

    [Fact]
    public async Task FanOut_returns_not_found_for_missing_account_or_event()
    {
        _clientsByAccount["acc-src"] = _sourceClient.Object;
        _accounts.Setup(a => a.GetAsync("acc-x", It.IsAny<CancellationToken>()))
            .ReturnsAsync((CalendarAccount?)null);
        (await Sut().FanOutAsync("acc-x", "ev", Destinations(), null))
            .ErrorCode.Should().Be("source_account_not_found");

        _accounts.Setup(a => a.GetAsync("acc-src", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Account("acc-src", AccountScope.Read));
        _sourceClient.Setup(c => c.GetEventAsync("gone", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SourceEventSnapshot?)null);
        (await Sut().FanOutAsync("acc-src", "gone", Destinations(), null))
            .ErrorCode.Should().Be("source_event_not_found");
    }

    [Fact]
    public async Task FanOut_collects_per_destination_failures_without_aborting_the_rest()
    {
        SetupHappyPath(Snapshot());
        var destB = new Mock<IReplicaGraphClient>();
        _clientsByAccount["acc-dst-b"] = destB.Object;
        _accounts.Setup(a => a.GetAsync("acc-dst-b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Account("acc-dst-b"));
        destB.Setup(c => c.CreateReplicaAsync("cal-b", It.IsAny<ReplicaDraft>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GraphRequestException("boom"));

        var outcome = await Sut().FanOutAsync("acc-src", "graph-ev-1", new List<ReplicaDestinationRequest>
        {
            new("acc-dst-b", "cal-b", "Hold"),
            new("acc-dst", "cal-dst", "Busy"),
        }, null);

        outcome.Created.Should().ContainSingle(l => l.DestinationAccountId == "acc-dst");
        outcome.Failures.Should().ContainSingle(f => f.Contains("cal-b"));
    }

    [Fact]
    public async Task RemoveAsync_active_link_deletes_the_remote_event_and_tombstones()
    {
        _clientsByAccount["acc-dst"] = _destClient.Object;
        var link = ExistingLink(ReplicaLinkStatus.Active);
        _links.Setup(l => l.GetAsync("link-1", It.IsAny<CancellationToken>())).ReturnsAsync(link);
        _links.Setup(l => l.UpdateAsync(It.Is<ReplicaLink>(x => x.Status == ReplicaLinkStatus.Tombstone),
            It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _destClient.Setup(c => c.DeleteEventAsync("dst-ev-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var outcome = await Sut().RemoveAsync("link-1");

        outcome.ErrorCode.Should().BeNull();
        _destClient.Verify(c => c.DeleteEventAsync("dst-ev-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAsync_broken_link_discards_without_touching_graph()
    {
        _clientsByAccount["acc-dst"] = _destClient.Object;
        var link = ExistingLink(ReplicaLinkStatus.Broken);
        _links.Setup(l => l.GetAsync("link-1", It.IsAny<CancellationToken>())).ReturnsAsync(link);
        _links.Setup(l => l.UpdateAsync(It.Is<ReplicaLink>(x => x.Status == ReplicaLinkStatus.Tombstone),
            It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var outcome = await Sut().RemoveAsync("link-1");

        outcome.ErrorCode.Should().BeNull();
        _destClient.Verify(c => c.DeleteEventAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "the replica is already gone — discarding only closes the link");
    }

    [Fact]
    public async Task UpdateTitleAsync_renames_the_replica_and_persists_the_new_mask()
    {
        _clientsByAccount["acc-dst"] = _destClient.Object;
        var link = ExistingLink(ReplicaLinkStatus.Active);
        _links.Setup(l => l.GetAsync("link-1", It.IsAny<CancellationToken>())).ReturnsAsync(link);
        _links.Setup(l => l.UpdateAsync(It.Is<ReplicaLink>(x => x.MaskTitle == "Focus"),
            It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _destClient.Setup(c => c.UpdateSubjectAsync("dst-ev-1", "Focus", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var outcome = await Sut().UpdateTitleAsync("link-1", "Focus");

        outcome.ErrorCode.Should().BeNull();
        _destClient.Verify(c => c.UpdateSubjectAsync("dst-ev-1", "Focus", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecreateAsync_broken_link_creates_a_new_event_and_reactivates()
    {
        _clientsByAccount["acc-src"] = _sourceClient.Object;
        _clientsByAccount["acc-dst"] = _destClient.Object;
        var link = ExistingLink(ReplicaLinkStatus.Broken);
        _links.Setup(l => l.GetAsync("link-1", It.IsAny<CancellationToken>())).ReturnsAsync(link);
        _sourceClient.Setup(c => c.GetEventAsync("graph-ev-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot());
        _destClient.Setup(c => c.CreateReplicaAsync("cal-dst", It.IsAny<ReplicaDraft>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("dst-ev-NEW");
        _links.Setup(l => l.UpdateAsync(It.Is<ReplicaLink>(x =>
                x.Status == ReplicaLinkStatus.Active && x.DestinationEventId == "dst-ev-NEW"),
            It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var outcome = await Sut().RecreateAsync("link-1");

        outcome.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task RecreateAsync_rejects_a_link_that_is_not_broken()
    {
        var link = ExistingLink(ReplicaLinkStatus.Active);
        _links.Setup(l => l.GetAsync("link-1", It.IsAny<CancellationToken>())).ReturnsAsync(link);

        var outcome = await Sut().RecreateAsync("link-1");

        outcome.ErrorCode.Should().Be("link_not_broken");
    }

    private static ReplicaLink ExistingLink(ReplicaLinkStatus status) => new()
    {
        Id = "link-1",
        UserId = "user-1",
        SourceAccountId = "acc-src",
        SourceEventId = "stable-1",
        SourceGraphEventId = "graph-ev-1",
        DestinationAccountId = "acc-dst",
        DestinationCalendarId = "cal-dst",
        DestinationEventId = "dst-ev-1",
        MaskTitle = "Busy",
        ContentHash = "AAA",
        Status = status,
    };
}
