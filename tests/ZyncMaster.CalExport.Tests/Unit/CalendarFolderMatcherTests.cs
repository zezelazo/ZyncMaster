using System;
using System.Collections.Generic;
using ZyncMaster.CalExport;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.CalExport.Tests;

public sealed class CalendarFolderMatcherTests
{
    private readonly CalendarFolderMatcher _sut = new CalendarFolderMatcher();

    private static IReadOnlyList<CalendarFolderInfo> MakeFolders(params string[] names)
    {
        var list = new List<CalendarFolderInfo>();
        for (int i = 0; i < names.Length; i++)
            list.Add(new CalendarFolderInfo { DisplayName = names[i], EntryId = $"eid{i}", StoreId = $"sid{i}" });
        return list;
    }

    [Fact]
    public void AllNamesFound_ReturnsMatchedList_OnNotFoundNotCalled()
    {
        var available   = MakeFolders("Work", "Personal");
        var notFoundLog = new List<string>();

        var result = _sut.Match(new[] { "Work", "Personal" }, available, notFoundLog.Add);

        result.Should().NotBeNull();
        result!.Count.Should().Be(2);
        notFoundLog.Should().BeEmpty();
    }

    [Fact]
    public void OneMissing_OnNotFoundCalledWithMissingName_ReturnsPartialList()
    {
        var available   = MakeFolders("Work", "Personal");
        var notFoundLog = new List<string>();

        var result = _sut.Match(new[] { "Work", "Missing" }, available, notFoundLog.Add);

        result.Should().NotBeNull();
        result!.Count.Should().Be(1);
        result[0].DisplayName.Should().Be("Work");
        notFoundLog.Should().ContainSingle().Which.Should().Be("Missing");
    }

    [Fact]
    public void AllMissing_ReturnsNull_OnNotFoundCalledForEach()
    {
        var available   = MakeFolders("Work", "Personal");
        var notFoundLog = new List<string>();

        var result = _sut.Match(new[] { "Gone", "Also Gone" }, available, notFoundLog.Add);

        result.Should().BeNull();
        notFoundLog.Should().BeEquivalentTo(new[] { "Gone", "Also Gone" });
    }

    [Fact]
    public void CaseInsensitiveMatch()
    {
        var available = MakeFolders("Work Calendar");

        var result = _sut.Match(new[] { "work calendar" }, available);

        result.Should().NotBeNull();
        result!.Count.Should().Be(1);
        result[0].DisplayName.Should().Be("Work Calendar");
    }

    [Fact]
    public void NoDuplicatesInResult()
    {
        var available = MakeFolders("Work");

        var result = _sut.Match(new[] { "Work", "work" }, available);

        result.Should().NotBeNull();
        result!.Count.Should().Be(1);
    }

    [Fact]
    public void EmptyRequestedNames_ReturnsNull()
    {
        var available = MakeFolders("Work", "Personal");

        var result = _sut.Match(System.Array.Empty<string>(), available);

        result.Should().BeNull();
    }

    [Fact]
    public void OnNotFoundIsOptional_NoExceptionWhenNull()
    {
        var available = MakeFolders("Work");

        var result = _sut.Match(new[] { "Missing" }, available, onNotFound: null);

        result.Should().BeNull();
    }

    [Fact]
    public void MultipleRequestedMatchingSameFolder_DeduplicatedByEntryId()
    {
        var available = MakeFolders("Work", "Personal", "Holidays");

        var result = _sut.Match(new[] { "Work", "Personal", "Work" }, available);

        result.Should().NotBeNull();
        result!.Count.Should().Be(2);
    }

    [Fact]
    public void NullRequestedNames_ThrowsArgumentNullException()
    {
        var available = MakeFolders("Work");

        Action act = () => _sut.Match(null!, available);

        act.Should().Throw<ArgumentNullException>()
           .Which.ParamName.Should().Be("requestedNames");
    }

    [Fact]
    public void NullAvailable_ThrowsArgumentNullException()
    {
        Action act = () => _sut.Match(new[] { "Work" }, null!);

        act.Should().Throw<ArgumentNullException>()
           .Which.ParamName.Should().Be("available");
    }

    [Fact]
    public void EmptyAvailableList_AllMissing_ReturnsNull()
    {
        var available = MakeFolders();

        var result = _sut.Match(new[] { "Work" }, available);

        result.Should().BeNull();
    }

    [Fact]
    public void SingleMatch_ReturnsSingleItemList()
    {
        var available = MakeFolders("Work", "Personal", "Holidays");

        var result = _sut.Match(new[] { "Personal" }, available);

        result.Should().NotBeNull();
        result!.Count.Should().Be(1);
        result[0].DisplayName.Should().Be("Personal");
    }
}
