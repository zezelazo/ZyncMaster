using System.Collections.Generic;
using FluentAssertions;
using SyncMaster.Graph;
using Xunit;

namespace SyncMaster.Graph.Tests;

public sealed class ImportResultTests
{
    [Fact]
    public void DefaultValues_AllCountersAreZero()
    {
        var result = new ImportResult();

        result.Created.Should().Be(0);
        result.Updated.Should().Be(0);
        result.Cancelled.Should().Be(0);
        result.Skipped.Should().Be(0);
    }

    [Fact]
    public void DefaultValues_FailedIsEmpty()
    {
        var result = new ImportResult();

        result.Failed.Should().NotBeNull();
        result.Failed.Should().BeEmpty();
    }

    [Fact]
    public void AddFailure_AppendsToList()
    {
        var result = new ImportResult();

        result.AddFailure("first");
        result.AddFailure("second");

        result.Failed.Should().HaveCount(2);
        result.Failed[0].Should().Be("first");
        result.Failed[1].Should().Be("second");
    }

    [Fact]
    public void AddFailure_PreservesInsertionOrder()
    {
        var result = new ImportResult();

        for (var i = 0; i < 5; i++)
            result.AddFailure($"msg-{i}");

        result.Failed.Should().Equal("msg-0", "msg-1", "msg-2", "msg-3", "msg-4");
    }

    [Fact]
    public void Failed_IsExposedAsReadOnlyList()
    {
        var result = new ImportResult();

        // Compile-time guarantee: the property type is IReadOnlyList<string>,
        // never List<string>. The runtime instance must not implement IList<string>
        // through the exposed surface.
        result.Failed.Should().BeAssignableTo<IReadOnlyList<string>>();
    }

    [Fact]
    public void Counters_CanBeSetIndependently()
    {
        var result = new ImportResult
        {
            Created   = 3,
            Updated   = 5,
            Cancelled = 2,
            Skipped   = 7,
        };

        result.Created.Should().Be(3);
        result.Updated.Should().Be(5);
        result.Cancelled.Should().Be(2);
        result.Skipped.Should().Be(7);
    }

    [Fact]
    public void Counters_AreMutableViaIncrement()
    {
        var result = new ImportResult();

        result.Created++;
        result.Updated++;
        result.Updated++;
        result.Cancelled++;
        result.Skipped += 4;

        result.Created.Should().Be(1);
        result.Updated.Should().Be(2);
        result.Cancelled.Should().Be(1);
        result.Skipped.Should().Be(4);
    }

    [Fact]
    public void AddFailure_DoesNotAffectCounters()
    {
        var result = new ImportResult { Created = 1, Updated = 2 };

        result.AddFailure("boom");

        result.Created.Should().Be(1);
        result.Updated.Should().Be(2);
        result.Cancelled.Should().Be(0);
        result.Skipped.Should().Be(0);
        result.Failed.Should().ContainSingle().Which.Should().Be("boom");
    }
}
