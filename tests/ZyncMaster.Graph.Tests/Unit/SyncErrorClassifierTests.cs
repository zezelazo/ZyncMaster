using System;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.Graph.Tests;

public sealed class SyncErrorClassifierTests
{
    [Fact]
    public void Null_throws()
    {
        Action act = () => SyncErrorClassifier.Classify(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("Graph transient error after 3 attempts: 429 Too Many Requests. URL=...")]
    [InlineData("Graph transient error after 3 attempts: 503 Service Unavailable. URL=...")]
    [InlineData("Graph request timed out after 3 attempts. URL=...")]
    [InlineData("Graph transport error after 3 attempts: connection reset. URL=...")]
    public void GraphRequest_transient_messages_are_Transient(string message)
    {
        SyncErrorClassifier.Classify(new GraphRequestException(message))
            .Should().Be(SyncErrorKind.Transient);
    }

    [Theory]
    [InlineData("Graph request failed: 429 Too Many Requests. URL=...")]
    [InlineData("Graph request failed: 500 Internal Server Error. URL=...")]
    [InlineData("Graph request failed: 502 Bad Gateway. URL=...")]
    [InlineData("Graph request failed: 503 Service Unavailable. URL=...")]
    [InlineData("Graph request failed: 504 Gateway Timeout. URL=...")]
    [InlineData("Graph request failed: 408 Request Timeout. URL=...")]
    public void GraphRequest_transient_status_codes_are_Transient(string message)
    {
        SyncErrorClassifier.Classify(new GraphRequestException(message))
            .Should().Be(SyncErrorKind.Transient);
    }

    [Fact]
    public void AuthenticationFailed_is_UserRecoverable()
    {
        SyncErrorClassifier.Classify(new AuthenticationFailedException("token expired"))
            .Should().Be(SyncErrorKind.UserRecoverable);
    }

    [Theory]
    [InlineData("Graph request failed: 401 Unauthorized. URL=...")]
    [InlineData("Graph request failed: 403 Forbidden. URL=...")]
    [InlineData("Graph request failed: 404 Not Found. URL=...")]
    public void GraphRequest_auth_and_missing_destination_are_UserRecoverable(string message)
    {
        SyncErrorClassifier.Classify(new GraphRequestException(message))
            .Should().Be(SyncErrorKind.UserRecoverable);
    }

    [Theory]
    [InlineData("Graph rejected the token: insufficient_scope")]
    [InlineData("InvalidAuthenticationToken: token is expired")]
    public void GraphRequest_auth_marker_messages_are_UserRecoverable(string message)
    {
        SyncErrorClassifier.Classify(new GraphRequestException(message))
            .Should().Be(SyncErrorKind.UserRecoverable);
    }

    [Fact]
    public void Raw_transport_exception_is_Transient()
    {
        SyncErrorClassifier.Classify(new HttpRequestException("socket reset"))
            .Should().Be(SyncErrorKind.Transient);
    }

    [Fact]
    public void Raw_timeout_is_Transient()
    {
        SyncErrorClassifier.Classify(new TaskCanceledException())
            .Should().Be(SyncErrorKind.Transient);
    }

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(FormatException))]
    public void Config_and_payload_errors_are_Fatal(Type exType)
    {
        var ex = (Exception)Activator.CreateInstance(exType, "boom")!;
        SyncErrorClassifier.Classify(ex).Should().Be(SyncErrorKind.Fatal);
    }

    [Fact]
    public void Unrecognized_GraphRequest_is_Fatal()
    {
        SyncErrorClassifier.Classify(new GraphRequestException("Graph returned an event without an id for source id 'x'."))
            .Should().Be(SyncErrorKind.Fatal);
    }

    // Fix 2 — the typed IsTransient flag drives the classification, NOT the message wording.
    // A GraphRequestException flagged transient is Transient even when its message carries no
    // "transient"/"timed out"/status-code marker at all.
    [Fact]
    public void Typed_transient_flag_is_Transient_regardless_of_message()
    {
        SyncErrorClassifier.Classify(
                new GraphRequestException("some completely arbitrary wording with no markers", isTransient: true))
            .Should().Be(SyncErrorKind.Transient);
    }

    // Fix 2 — a typed transient with an inner exception and an arbitrary message is still
    // Transient (mirrors the read-truncation / transport throws that wrap an inner cause).
    [Fact]
    public void Typed_transient_with_inner_is_Transient()
    {
        SyncErrorClassifier.Classify(
                new GraphRequestException("arbitrary", new InvalidOperationException("inner"), isTransient: true))
            .Should().Be(SyncErrorKind.Transient);
    }

    // Fix 2 — the read-truncation message shape (which is wired with isTransient:true) classifies
    // as Transient via the typed flag, not via any wording match.
    [Fact]
    public void Truncated_read_message_is_Transient_via_typed_flag()
    {
        SyncErrorClassifier.Classify(new GraphRequestException(
                "Graph paged read returned a 2xx response with no 'value' collection; " +
                "treating as a truncated read rather than end-of-pages. URL=...",
                isTransient: true))
            .Should().Be(SyncErrorKind.Transient);
    }

    // Fix 2 — an unflagged GraphRequestException with no recognizable markers stays Fatal: the
    // typed flag is the ONLY thing that makes a Graph failure transient now (wording is fallback).
    [Fact]
    public void Untyped_unrecognized_GraphRequest_stays_Fatal()
    {
        SyncErrorClassifier.Classify(
                new GraphRequestException("no markers here", isTransient: false))
            .Should().Be(SyncErrorKind.Fatal);
    }
}
