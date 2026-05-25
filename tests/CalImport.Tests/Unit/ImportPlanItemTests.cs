using System;
using FluentAssertions;
using SyncMaster.CalImport;
using SyncMaster.Core;
using Xunit;

namespace SyncMaster.CalImport.Tests;

public sealed class ImportPlanItemTests
{
    private static AppointmentRecord MakeRecord(string id = "id-1")
        => new AppointmentRecord { Id = id, Subject = "X" };

    [Fact]
    public void ForCreate_SetsActionAndNulls()
    {
        var record = MakeRecord();

        var item = ImportPlanItem.ForCreate(record);

        item.Record.Should().BeSameAs(record);
        item.Action.Should().Be(ImportAction.Create);
        item.ExistingEventId.Should().BeNull();
        item.ExistingBodyHtml.Should().BeNull();
    }

    [Fact]
    public void ForUpdate_NullExistingId_Throws()
    {
        Action act = () => ImportPlanItem.ForUpdate(MakeRecord(), existingEventId: null!, existingBodyHtml: "");

        act.Should().Throw<ArgumentException>().WithParameterName("existingEventId");
    }

    [Fact]
    public void ForUpdate_EmptyExistingId_Throws()
    {
        Action act = () => ImportPlanItem.ForUpdate(MakeRecord(), existingEventId: "", existingBodyHtml: "");

        act.Should().Throw<ArgumentException>().WithParameterName("existingEventId");
    }

    [Fact]
    public void ForUpdate_NullExistingBody_Throws()
    {
        Action act = () => ImportPlanItem.ForUpdate(MakeRecord(), existingEventId: "graph-1", existingBodyHtml: null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("existingBodyHtml");
    }

    [Fact]
    public void ForUpdate_ValidArgs_Sets()
    {
        var record = MakeRecord();

        var item = ImportPlanItem.ForUpdate(record, "graph-1", "<body/>");

        item.Record.Should().BeSameAs(record);
        item.Action.Should().Be(ImportAction.Update);
        item.ExistingEventId.Should().Be("graph-1");
        item.ExistingBodyHtml.Should().Be("<body/>");
    }

    [Fact]
    public void ForCancel_NullExistingId_Throws()
    {
        Action act = () => ImportPlanItem.ForCancel(MakeRecord(), existingEventId: null!);

        act.Should().Throw<ArgumentException>().WithParameterName("existingEventId");
    }

    [Fact]
    public void ForCancel_EmptyExistingId_Throws()
    {
        Action act = () => ImportPlanItem.ForCancel(MakeRecord(), existingEventId: "");

        act.Should().Throw<ArgumentException>().WithParameterName("existingEventId");
    }

    [Fact]
    public void ForCancel_ValidArgs_Sets()
    {
        var record = MakeRecord();

        var item = ImportPlanItem.ForCancel(record, "graph-1");

        item.Record.Should().BeSameAs(record);
        item.Action.Should().Be(ImportAction.Cancel);
        item.ExistingEventId.Should().Be("graph-1");
        item.ExistingBodyHtml.Should().BeNull();
    }

    [Fact]
    public void ForSkip_SetsActionAndNulls()
    {
        var record = MakeRecord();

        var item = ImportPlanItem.ForSkip(record);

        item.Record.Should().BeSameAs(record);
        item.Action.Should().Be(ImportAction.Skip);
        item.ExistingEventId.Should().BeNull();
        item.ExistingBodyHtml.Should().BeNull();
    }
}
