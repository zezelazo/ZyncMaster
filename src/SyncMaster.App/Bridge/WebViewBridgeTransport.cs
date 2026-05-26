using System;
using SyncMaster.App.Windows;

namespace SyncMaster.App.Bridge;

// IBridgeTransport over the web host's JS messaging channel.
//
// With the loopback WebHost the host already exposes the bidirectional channel
// (/__bridge/send for web->native, /__bridge/poll for native->web), so this transport
// is a thin adapter that forwards Send/Received to the underlying IBridgeTransport the
// host provides. When a real embedded WebView is wired later (WebView2 on Windows,
// WKWebView on macOS), this class is where its postMessage / window.chrome.webview
// channel gets adapted to the same IBridgeTransport surface UiBridge consumes — nothing
// else in the app changes.
public sealed class WebViewBridgeTransport : IBridgeTransport
{
    private readonly IBridgeTransport _inner;

    public WebViewBridgeTransport(WebHost host)
    {
        if (host == null) throw new ArgumentNullException(nameof(host));
        _inner = host; // WebHost implements IBridgeTransport directly.
    }

    // Overload that accepts the transport surface directly, for tests and future
    // WebView-backed hosts that aren't the loopback WebHost.
    public WebViewBridgeTransport(IBridgeTransport inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public event Action<string>? Received
    {
        add => _inner.Received += value;
        remove => _inner.Received -= value;
    }

    public void Send(string json) => _inner.Send(json);
}
