using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Core;
using Xunit;

namespace ZyncMaster.Core.Tests;

public sealed class TransientNetworkErrorTests
{
    private static HttpRequestException Wrap(SocketException inner) =>
        new("An error occurred while sending the request.", inner);

    [Theory]
    [InlineData(SocketError.HostNotFound,      "DNS lookup failed")]   // 11001 — post-resume resolver not up
    [InlineData(SocketError.ConnectionReset,   "connection reset")]    // 10054 — server restarting mid-deploy
    [InlineData(SocketError.TimedOut,          "timed out")]           // 10060
    [InlineData(SocketError.ConnectionRefused, "refused")]
    [InlineData(SocketError.NetworkUnreachable, "unreachable")]
    public void Describe_KnownSocketErrors_ReturnsConciseReason(SocketError code, string expected)
    {
        var desc = TransientNetworkError.Describe(Wrap(new SocketException((int)code)));

        desc.Should().NotBeNull();
        desc.Should().ContainEquivalentOf(expected);
    }

    [Fact]
    public void Describe_SocketErrorNestedUnderIOException_IsStillClassified()
    {
        // The real shape logged in the field: HttpRequestException -> IOException -> SocketException.
        var ex = new HttpRequestException("send failed",
            new System.IO.IOException("Unable to read data from the transport connection",
                new SocketException((int)SocketError.ConnectionReset)));

        TransientNetworkError.Describe(ex).Should().ContainEquivalentOf("connection reset");
    }

    [Fact]
    public void Describe_HttpClientTimeout_IsTransient()
    {
        var ex = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout",
            new TimeoutException());

        TransientNetworkError.Describe(ex).Should().ContainEquivalentOf("timed out");
    }

    [Fact]
    public void Describe_NonNetworkExceptions_ReturnNull()
    {
        TransientNetworkError.Describe(new InvalidOperationException("logic bug")).Should().BeNull();
        TransientNetworkError.Describe(new NullReferenceException()).Should().BeNull();
        // A plain cancellation (user/shutdown) is not a network transient.
        TransientNetworkError.Describe(new TaskCanceledException("cancelled")).Should().BeNull();
    }

    [Fact]
    public void Describe_UnrecognizedSocketError_StillNamesTheCode()
    {
        var desc = TransientNetworkError.Describe(Wrap(new SocketException((int)SocketError.ProtocolNotSupported)));

        desc.Should().NotBeNull();
        desc.Should().ContainEquivalentOf("socket error");
    }
}
