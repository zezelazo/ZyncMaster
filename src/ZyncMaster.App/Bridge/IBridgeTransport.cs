using System;

namespace ZyncMaster.App.Bridge;

// The wire between the native UiBridge and the web layer. Send pushes a JSON string
// to the web side; Received is raised with each inbound JSON string from the web side.
// WebHost implements this over its loopback channel; a real WebView would implement it
// over its postMessage bridge.
public interface IBridgeTransport
{
    void Send(string json);
    event Action<string>? Received;
}
