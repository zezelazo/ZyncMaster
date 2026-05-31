using System;
using System.Net.Http;
using FluentAssertions;
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// Fix 3 (§A-3) — the /api/pairs/{id}/run source read is wrapped so a TRANSIENT read failure
// (throttling, 5xx, timeout, transport drop, or the Fix-1 truncated/malformed paged read)
// aborts BEFORE the destructive mirror and is surfaced as Partial (retry later) instead of a
// 500. These cover the load-bearing decision (IsTransientReadFailure) and the Partial shape
// (PartialReadResult), which the endpoint applies in its read catch + RecordRunAsync path.
public sealed class PairRunReadFailureTests
{
    [Fact]
    public void Truncated_read_is_classified_transient_so_mirror_is_skipped()
    {
        // The Fix-1 truncated-read error is a typed-transient GraphRequestException.
        var truncated = new GraphRequestException(
            "Graph calendarView page returned a 2xx response with no 'value' collection; " +
            "treating as a truncated read rather than end-of-pages. URL=...",
            isTransient: true);

        PairEndpoints.IsTransientReadFailure(truncated)
            .Should().BeTrue("a truncated read must abort before the mirror and report Partial");
    }

    [Fact]
    public void Throttling_read_failure_is_transient()
    {
        PairEndpoints.IsTransientReadFailure(
                new GraphRequestException("Graph transient error after 3 attempts: 429 Too Many Requests. URL=...", isTransient: true))
            .Should().BeTrue();
    }

    [Fact]
    public void Raw_transport_read_failure_is_transient()
    {
        PairEndpoints.IsTransientReadFailure(new HttpRequestException("socket reset"))
            .Should().BeTrue();
    }

    [Fact]
    public void Auth_read_failure_is_not_transient_and_propagates()
    {
        // UserRecoverable (auth/consent) is NOT a Partial: it must propagate so the user is
        // told to reconnect, not silently retried forever.
        PairEndpoints.IsTransientReadFailure(new AuthenticationFailedException("token expired"))
            .Should().BeFalse();
    }

    [Fact]
    public void Fatal_read_failure_is_not_transient_and_propagates()
    {
        PairEndpoints.IsTransientReadFailure(new InvalidOperationException("bad config"))
            .Should().BeFalse();
    }

    [Fact]
    public void Cancellation_is_never_treated_as_transient()
    {
        PairEndpoints.IsTransientReadFailure(new OperationCanceledException())
            .Should().BeFalse("a caller cancellation must surface as cancellation, not a retryable Partial");
    }

    [Fact]
    public void PartialReadResult_has_partial_shape_with_zero_counts_and_the_error()
    {
        var ex = new GraphRequestException("Graph transient error after 3 attempts: 503. URL=...", isTransient: true);

        var result = PairEndpoints.PartialReadResult(ex);

        result.Partial.Should().BeTrue();
        result.Created.Should().Be(0);
        result.Updated.Should().Be(0);
        result.Deleted.Should().Be(0);
        result.Skipped.Should().Be(0);
        result.Failures.Should().ContainSingle()
            .Which.Should().Contain("transient").And.Contain(ex.Message);
    }
}
