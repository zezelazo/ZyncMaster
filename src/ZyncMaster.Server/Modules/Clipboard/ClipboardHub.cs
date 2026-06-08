using System.Net.WebSockets;

namespace ZyncMaster.Server;

// The server-side receive loop for a clipboard WebSocket. In F1a the client publishes items and
// relays keys via REST, so this loop carries no inbound application protocol — it just drains and
// discards inbound frames (keepalive / ping payloads) until the socket closes, then returns. The
// endpoint's finally block is responsible for removing the connection from the registry and
// re-broadcasting presence; this method does not touch the registry. Kept deliberately minimal
// and robust: a Close frame, cancellation, or any exception ends the loop cleanly.
public static class ClipboardHub
{
    private const int ReceiveBufferSize = 4 * 1024;

    public static async Task RunReceiveLoopAsync(
        ClipboardConnection conn,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conn);

        var buffer = new byte[ReceiveBufferSize];
        try
        {
            while (!ct.IsCancellationRequested && conn.Socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await conn.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (WebSocketException)
                {
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                // Inbound data frames are drained and ignored in F1a.
            }
        }
        catch
        {
            // Any unexpected failure ends the loop; the endpoint's finally handles cleanup.
        }
    }
}
