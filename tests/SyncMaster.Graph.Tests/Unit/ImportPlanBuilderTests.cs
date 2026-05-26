using System;
using System.Collections.Generic;
using FluentAssertions;
using SyncMaster.Graph;
using SyncMaster.Core;
using Xunit;

namespace SyncMaster.Graph.Tests;

public sealed class ImportPlanBuilderTests
{
    private readonly ImportPlanBuilder _sut = new ImportPlanBuilder();

    private static AppointmentRecord MakeRecord(string id = "id-1", bool cancelled = false)
        => new AppointmentRecord { Id = id, Subject = "X", IsCancelled = cancelled };

    private static ExistingEventLookup MakeExisting(string graphId = "graph-1", string body = "")
        => new ExistingEventLookup { Id = graphId, BodyHtml = body };

    [Fact]
    public void NotCancelled_NoMatch_ActionIsCreate()
    {
        var plan = _sut.Build(new[] { MakeRecord("a") }, new Dictionary<string, ExistingEventLookup>());

        plan.Should().HaveCount(1);
        plan[0].Action.Should().Be(ImportAction.Create);
        plan[0].ExistingEventId.Should().BeNull();
    }

    [Fact]
    public void NotCancelled_WithMatch_ActionIsUpdate()
    {
        var existing = new Dictionary<string, ExistingEventLookup>
        {
            ["a"] = MakeExisting("graph-a", "<body>existing</body>")
        };

        var plan = _sut.Build(new[] { MakeRecord("a") }, existing);

        plan[0].Action.Should().Be(ImportAction.Update);
        plan[0].ExistingEventId.Should().Be("graph-a");
        plan[0].ExistingBodyHtml.Should().Be("<body>existing</body>");
    }

    [Fact]
    public void Cancelled_WithMatch_ActionIsCancel()
    {
        var existing = new Dictionary<string, ExistingEventLookup>
        {
            ["a"] = MakeExisting("graph-a")
        };

        var plan = _sut.Build(new[] { MakeRecord("a", cancelled: true) }, existing);

        plan[0].Action.Should().Be(ImportAction.Cancel);
        plan[0].ExistingEventId.Should().Be("graph-a");
    }

    [Fact]
    public void Cancelled_NoMatch_ActionIsSkip()
    {
        var plan = _sut.Build(
            new[] { MakeRecord("a", cancelled: true) },
            new Dictionary<string, ExistingEventLookup>());

        plan[0].Action.Should().Be(ImportAction.Skip);
        plan[0].ExistingEventId.Should().BeNull();
    }

    [Fact]
    public void MixedBatch_BuildsCorrectActions()
    {
        var records = new[]
        {
            MakeRecord("create"),
            MakeRecord("update"),
            MakeRecord("cancel", cancelled: true),
            MakeRecord("skip",   cancelled: true),
        };
        var existing = new Dictionary<string, ExistingEventLookup>
        {
            ["update"] = MakeExisting("g-update"),
            ["cancel"] = MakeExisting("g-cancel"),
        };

        var plan = _sut.Build(records, existing);

        plan.Should().HaveCount(4);
        plan[0].Action.Should().Be(ImportAction.Create);
        plan[1].Action.Should().Be(ImportAction.Update);
        plan[2].Action.Should().Be(ImportAction.Cancel);
        plan[3].Action.Should().Be(ImportAction.Skip);
    }

    [Fact]
    public void Build_NullRecords_Throws()
    {
        Action act = () => _sut.Build(null!, new Dictionary<string, ExistingEventLookup>());
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("records");
    }

    [Fact]
    public void Build_NullExistingMap_Throws()
    {
        Action act = () => _sut.Build(new[] { MakeRecord("a") }, null!);
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("existingMap");
    }

    [Fact]
    public void Build_RecordWithEmptyId_MatchesExistingEmptyKey()
    {
        // Pathological case: appointment record with empty Id, and the existingMap
        // also has an empty-string key. The lookup must hit, producing an Update.
        var existing = new Dictionary<string, ExistingEventLookup>
        {
            [""] = MakeExisting("graph-empty", "<p>old</p>"),
        };
        var plan = _sut.Build(new[] { MakeRecord(id: "") }, existing);

        plan.Should().HaveCount(1);
        plan[0].Action.Should().Be(ImportAction.Update);
        plan[0].ExistingEventId.Should().Be("graph-empty");
        plan[0].ExistingBodyHtml.Should().Be("<p>old</p>");
    }

    [Fact]
    public void Build_ExistingBodyHtmlIsNull_FallsBackToEmpty()
    {
        // ExistingEventLookup.BodyHtml is non-nullable in the model but the Build
        // path coalesces with ?? "". Force a null via reflection-friendly init.
        var existing = new Dictionary<string, ExistingEventLookup>
        {
            ["a"] = new ExistingEventLookup { Id = "graph-a", BodyHtml = null! },
        };

        var plan = _sut.Build(new[] { MakeRecord("a") }, existing);

        plan[0].Action.Should().Be(ImportAction.Update);
        plan[0].ExistingBodyHtml.Should().Be("");
    }
}
