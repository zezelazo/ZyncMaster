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

public class PrefixRuleEvaluatorTests
{
    private readonly Mock<ICalendarAccountStore> _accounts = new();
    private readonly Mock<IReplicaLinkStore> _links = new();
    private readonly Mock<IReplicaGraphClient> _client = new();
    private readonly Mock<IReplicaGraphClient> _destClient = new();

    private PrefixRuleEvaluator Sut()
    {
        _accounts.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => new CalendarAccount
            {
                Id = id,
                UserId = "user-1",
                Kind = AccountKind.Graph,
                Provider = "microsoft",
                AccountEmail = $"{id}@test",
                Scope = AccountScope.ReadWrite,
                ConnectedAt = DateTimeOffset.UtcNow,
            });
        _links.Setup(l => l.ListBySourceEventAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReplicaLink>());
        _links.Setup(l => l.AddAsync(It.IsAny<ReplicaLink>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReplicaLink l, CancellationToken _) => l);
        _destClient.Setup(c => c.CreateReplicaAsync(It.IsAny<string>(), It.IsAny<ReplicaDraft>(),
            It.IsAny<CancellationToken>())).ReturnsAsync("rep-1");

        var service = new ReplicaService(
            _accounts.Object, _links.Object,
            id => id == "acc-dst" ? _destClient.Object : _client.Object,
            TimeProvider.System);
        return new PrefixRuleEvaluator(service);
    }

    private static PrefixRule LunchRule(int sortOrder = 0, string id = "rule-1") => new()
    {
        Id = id,
        Prefix = "Lunch",
        MaskTitle = "Lunch",
        Enabled = true,
        SortOrder = sortOrder,
        Destinations = new[] { new PrefixRuleDestination("acc-dst", "cal-dst") },
    };

    private static SourceEventSnapshot Event(string subject) => new()
    {
        GraphEventId = "ev-1",
        StableId = "stable-1",
        Subject = subject,
        Start = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero),
        End = new DateTimeOffset(2026, 6, 15, 13, 0, 0, TimeSpan.Zero),
        ShowAs = "busy",
    };

    [Fact]
    public async Task Lunch_event_is_stripped_fanned_out_and_stamped_in_that_order()
    {
        var sut = Sut(); // first: Sut() registers a generic CreateReplicaAsync setup that would shadow the callback below
        var calls = new List<string>();
        _client.Setup(c => c.UpdateSubjectAsync("ev-1", "Pizza with Ana", It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("rename")).Returns(Task.CompletedTask);
        _destClient.Setup(c => c.CreateReplicaAsync("cal-dst", It.IsAny<ReplicaDraft>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("fanout")).ReturnsAsync("rep-1");
        _client.Setup(c => c.StampRuleProcessedAsync("ev-1", "rule-1", It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("stamp")).Returns(Task.CompletedTask);

        var summary = await sut.EvaluateAsync("acc-src",
            new[] { Event("[Lunch] Pizza with Ana") }, new[] { LunchRule() }, _client.Object);

        summary.RulesApplied.Should().Be(1);
        summary.ReplicasCreated.Should().Be(1);
        calls.Should().ContainInOrder(new[] { "rename", "fanout", "stamp" },
            "the stamp goes LAST so an interrupted pass retries (fan-out dedupes)");
    }

    [Fact]
    public async Task Replica_title_is_the_rule_mask_never_the_real_title()
    {
        var sut = Sut(); // first: Sut() registers a generic CreateReplicaAsync setup that would shadow the callback below
        ReplicaDraft? sent = null;
        _destClient.Setup(c => c.CreateReplicaAsync("cal-dst", It.IsAny<ReplicaDraft>(), It.IsAny<CancellationToken>()))
            .Callback((string _, ReplicaDraft d, CancellationToken _) => sent = d)
            .ReturnsAsync("rep-1");

        await sut.EvaluateAsync("acc-src",
            new[] { Event("[Lunch] Pizza with Ana") }, new[] { LunchRule() }, _client.Object);

        sent!.MaskTitle.Should().Be("Lunch", "PRIVACY: the fan-out carries the rule's mask");
        sent.MaskTitle.Should().NotContain("Pizza");
    }

    [Fact]
    public async Task Match_is_case_insensitive_on_the_exact_bracketed_prefix()
    {
        var summary = await Sut().EvaluateAsync("acc-src",
            new[] { Event("[lunch] Tacos") }, new[] { LunchRule() }, _client.Object);

        summary.RulesApplied.Should().Be(1);
        _client.Verify(c => c.UpdateSubjectAsync("ev-1", "Tacos", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("Lunch with Ana")]       // no brackets
    [InlineData("[Lunches] X")]          // different tag
    [InlineData("Meeting [Lunch] X")]    // not at the start
    public async Task Non_matching_subjects_are_left_completely_alone(string subject)
    {
        var summary = await Sut().EvaluateAsync("acc-src",
            new[] { Event(subject) }, new[] { LunchRule() }, _client.Object);

        summary.RulesApplied.Should().Be(0);
        _client.Verify(c => c.UpdateSubjectAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Bare_prefix_with_no_rest_renames_to_the_mask_title()
    {
        await Sut().EvaluateAsync("acc-src",
            new[] { Event("[Lunch]") }, new[] { LunchRule() }, _client.Object);

        _client.Verify(c => c.UpdateSubjectAsync("ev-1", "Lunch", It.IsAny<CancellationToken>()),
            Times.Once, "an empty stripped subject falls back to the rule's mask (plan decision 5)");
    }

    [Fact]
    public async Task Already_processed_events_are_skipped_strip_runs_exactly_once()
    {
        var summary = await Sut().EvaluateAsync("acc-src",
            new[] { Event("[Lunch] Pizza") with { RuleProcessedBy = "rule-1" } },
            new[] { LunchRule() }, _client.Object);

        summary.RulesApplied.Should().Be(0,
            "anti-loop 2: the ZmRuleProcessed stamp makes the rule fire at most once per event");
    }

    [Fact]
    public async Task Events_with_either_managed_mark_never_match_anti_loop()
    {
        var summary = await Sut().EvaluateAsync("acc-src",
            new[]
            {
                Event("[Lunch] A") with { HasReplicaMark = true },
                Event("[Lunch] B") with { HasCalImportMark = true },
            },
            new[] { LunchRule() }, _client.Object);

        summary.RulesApplied.Should().Be(0,
            "anti-loop 1: replicas and pair-mirror events are NEVER rule sources");
    }

    [Fact]
    public async Task Cancelled_events_are_skipped()
    {
        var summary = await Sut().EvaluateAsync("acc-src",
            new[] { Event("[Lunch] Pizza") with { IsCancelled = true } },
            new[] { LunchRule() }, _client.Object);

        summary.RulesApplied.Should().Be(0);
    }

    [Fact]
    public async Task First_rule_by_sort_order_wins_an_event_matches_at_most_one()
    {
        var sut = Sut(); // first: Sut() registers a generic CreateReplicaAsync setup that would shadow the callback below
        var first = LunchRule(sortOrder: 0, id: "rule-first") with { MaskTitle = "Win" };
        var second = LunchRule(sortOrder: 1, id: "rule-second") with { MaskTitle = "Lose" };
        ReplicaDraft? sent = null;
        _destClient.Setup(c => c.CreateReplicaAsync("cal-dst", It.IsAny<ReplicaDraft>(), It.IsAny<CancellationToken>()))
            .Callback((string _, ReplicaDraft d, CancellationToken _) => sent = d)
            .ReturnsAsync("rep-1");

        var summary = await sut.EvaluateAsync("acc-src",
            new[] { Event("[Lunch] Pizza") }, new[] { second, first }, _client.Object);

        summary.RulesApplied.Should().Be(1);
        sent!.MaskTitle.Should().Be("Win");
        _client.Verify(c => c.StampRuleProcessedAsync("ev-1", "rule-first", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Disabled_rules_never_fire()
    {
        var summary = await Sut().EvaluateAsync("acc-src",
            new[] { Event("[Lunch] Pizza") },
            new[] { LunchRule() with { Enabled = false } }, _client.Object);

        summary.RulesApplied.Should().Be(0);
    }
}
