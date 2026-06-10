using System;
using System.Net.Http;
using System.Net.Sockets;

namespace ZyncMaster.Core;

// Classifies exceptions that mean "the network/server was momentarily unreachable" — DNS not yet up
// after sleep/resume, a connection reset while the server restarts during a deploy, a plain timeout.
// Call sites that retry anyway (scheduler tick, health probe, clipboard start) use this to log ONE
// concise line instead of a 20-line stack trace per blip: the trace carries no actionable signal for
// a transient, and the repetition buries real failures in the daily log.
public static class TransientNetworkError
{
    // Returns a short human description when the exception is a transient network failure,
    // or null when it is anything else (the caller should then log the full exception).
    public static string? Describe(Exception ex)
    {
        // HttpRequestException wraps the interesting cause (IOException/SocketException); an
        // AggregateException can wrap either. Walk the chain and classify the deepest socket error.
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException se)
            {
                return se.SocketErrorCode switch
                {
                    SocketError.HostNotFound      => "DNS lookup failed (host not found — network likely still coming up)",
                    SocketError.TryAgain          => "DNS lookup failed (temporary resolver failure)",
                    SocketError.ConnectionReset   => "connection reset by the remote host",
                    SocketError.ConnectionRefused => "connection refused (server not listening yet)",
                    SocketError.TimedOut          => "connection timed out",
                    SocketError.NetworkUnreachable or SocketError.HostUnreachable
                                                  => "network unreachable",
                    _ => $"socket error {se.SocketErrorCode}",
                };
            }

            if (e is System.IO.IOException && e.InnerException is null && ex is HttpRequestException)
                return "connection dropped mid-request";
        }

        // A timeout surfaced by HttpClient (no socket error in the chain).
        if (ex is TaskCanceledException tce && tce.InnerException is TimeoutException)
            return "request timed out";

        return null;
    }
}
