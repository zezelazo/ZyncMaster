namespace SyncMaster.App.Windows;

// Abstraction over whatever actually renders the web UI. Today it is backed by a
// loopback HttpListener (see WebHost) that serves Assets/ui and opens it; swapping in
// a real embedded WebView (WebView2 on Windows, WKWebView on macOS) later means
// providing another IWebHost implementation and changing one line in the composition
// root — nothing else in the app references the concrete host.
public interface IWebHost
{
    // Loads the UI (Assets/ui/index.html) into the host surface or loopback URL.
    void Load();

    // Reloads the currently-loaded UI.
    void Reload();

    // Pushes a JSON message from native code into the web layer (status events, etc.).
    void PostToWeb(string json);
}
