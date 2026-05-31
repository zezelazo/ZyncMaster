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
}
