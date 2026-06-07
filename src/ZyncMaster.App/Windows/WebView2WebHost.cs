#if WIN_WEBVIEW2
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
using ZyncMaster.App.Bridge;

namespace ZyncMaster.App.Windows;

// Embedded WebView2 host (Windows only — compiled into the net10.0-windows target).
//
// It is a NativeControlHost: Avalonia hands it a child HWND, onto which we attach a
// CoreWebView2 controller. The bundled Assets/ui site is served through a virtual host
// mapping (https://zyncmaster.assets/...), and the UI's window.chrome.webview channel is
// bridged to the app's IBridgeTransport — WebView2 always injects window.chrome.webview,
// so the UI's native-bridge branch (postMessage / 'message' events) is what runs here.
//
// macOS will get a WKWebView-backed IWebHost behind this same seam; nothing else changes.
public sealed class WebView2WebHost : NativeControlHost, IWebHost, IBridgeTransport
{
    private const string VirtualHost = "zyncmaster.assets";

    // Where to download the Evergreen WebView2 Runtime. Surfaced to the user (and opened in the
    // system browser) by the native fallback panel when the runtime is missing.
    public const string RuntimeDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    // The page this host navigates to. The main dashboard uses index.html; the clipboard viewer
    // window reuses the SAME bundled site through this seam by passing "clipboard-viewer.html".
    private readonly string _startUrl;
    private readonly string _uiRoot;
    private readonly Queue<string> _pending = new();
    private CoreWebView2Controller? _controller;
    private bool _ready;
    private bool _initFailedRaised;

    public event Action<string>? Received;

    // FIX 2 — raised when the embedded WebView2 cannot initialise (most commonly because the
    // Evergreen WebView2 Runtime is not installed). The window subscribes to this to swap in a
    // NATIVE Avalonia panel explaining the problem with a link to the runtime download, instead of
    // leaving a blank frameless window with no explanation. Raised on the UI thread.
    public event Action? InitializationFailed;

    // True once the runtime is confirmed missing / init has failed. Lets the window decide whether
    // to show the fallback panel even if it attaches after the failure was raised.
    public bool HasFailed { get; private set; }

    // startPage: the file under the bundled site to navigate to (default "index.html"). The clipboard
    // viewer passes "clipboard-viewer.html" so its window renders the viewer page through the SAME
    // virtual-host mapping + bridge channel as the main window.
    public WebView2WebHost(string? uiRoot = null, string startPage = "index.html")
    {
        _uiRoot = uiRoot ?? DefaultUiRoot();
        var page = string.IsNullOrWhiteSpace(startPage) ? "index.html" : startPage.TrimStart('/');
        _startUrl = $"https://{VirtualHost}/{page}";
    }

    // Navigation is driven by the controller becoming ready (see InitAsync) once the
    // control is attached to the window. If already ready, (re)load the start page.
    public void Load()
    {
        if (_ready) _controller!.CoreWebView2.Navigate(_startUrl);
    }

    public void Reload()
    {
        if (_ready) _controller!.CoreWebView2.Reload();
    }

    public void PostToWeb(string json)
    {
        if (json == null) throw new ArgumentNullException(nameof(json));
        Dispatcher.UIThread.Post(() =>
        {
            if (_ready) _controller!.CoreWebView2.PostWebMessageAsString(json);
            else _pending.Enqueue(json);
        });
    }

    // IBridgeTransport.Send is the native->web direction, same channel as PostToWeb.
    public void Send(string json) => PostToWeb(json);

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        _ = InitAsync(handle.Handle);
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        try { _controller?.Close(); } catch { /* ignore */ }
        _controller = null;
        _ready = false;
        base.DestroyNativeControlCore(control);
    }

    private async Task InitAsync(IntPtr hwnd)
    {
        try
        {
            // Detect a missing Evergreen WebView2 Runtime BEFORE attempting to create the
            // environment: GetAvailableBrowserVersionString returns null (or throws) when no runtime
            // is installed. CreateAsync would otherwise throw a less obvious error and we'd land in
            // the catch below anyway, but checking first keeps the failure reason unambiguous.
            string? version = null;
            try { version = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch { version = null; }

            if (string.IsNullOrEmpty(version))
            {
                RaiseInitFailed();
                return;
            }

            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZyncMaster", "App", "WebView2");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            _controller = await env.CreateCoreWebView2ControllerAsync(hwnd);

            UpdateBounds();

            var core = _controller.CoreWebView2;

            // Let the web title bar mark draggable regions with CSS `app-region: drag`
            // (the reliable way to move a frameless window from WebView2 content).
            try { core.Settings.IsNonClientRegionSupportEnabled = true; }
            catch { /* older WebView2 runtime: the bridge drag fallback still applies */ }

            // Lock down the embedded browser chrome so the app does not look or behave like a
            // browser tab. In RELEASE there is no right-click context menu (no Reload/Save/
            // Inspect), no DevTools, no built-in error pages, and no Ctrl+/zoom — a stray
            // Reload would blow away the in-page app state. DEBUG keeps the context menu and
            // DevTools so developers can still inspect the WebView during development.
            try
            {
                var s = core.Settings;
#if DEBUG
                s.AreDefaultContextMenusEnabled = true;
                s.AreDevToolsEnabled = true;
#else
                s.AreDefaultContextMenusEnabled = false;
                s.AreDevToolsEnabled = false;
                s.IsZoomControlEnabled = false;
                s.IsBuiltInErrorPageEnabled = false;
                s.IsStatusBarEnabled = false;
#endif
            }
            catch { /* older WebView2 runtime may not expose every setting: best-effort */ }

            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, _uiRoot, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessageReceived;

            // External links in the UI use <a target="_blank"> (About: Website, What's new,
            // the DevLab-Pe company link). WebView2 blocks new windows unless a handler claims
            // them, so those clicks would silently do nothing. Cancel the in-WebView window and
            // hand the URL to the system browser instead. Only http/https is allowed — any other
            // scheme (file:, javascript:, etc.) is dropped so a hostile/odd link can't shell out.
            try { core.NewWindowRequested += OnNewWindowRequested; }
            catch { /* older WebView2 runtime: external links just won't open, no crash */ }

            core.Navigate(_startUrl);

            _ready = true;
            while (_pending.Count > 0)
                core.PostWebMessageAsString(_pending.Dequeue());
        }
        catch
        {
            // WebView2 runtime missing or init failed: surface a native fallback panel (FIX 2)
            // instead of leaving the host blank. The tray remains usable and the engine keeps
            // running headless regardless.
            _ready = false;
            RaiseInitFailed();
        }
    }

    // Marks the host failed and raises InitializationFailed once, on the UI thread. Idempotent so
    // the up-front runtime check and the catch block cannot double-fire it.
    private void RaiseInitFailed()
    {
        _ready = false;
        HasFailed = true;
        if (_initFailedRaised)
            return;
        _initFailedRaised = true;
        Dispatcher.UIThread.Post(() => InitializationFailed?.Invoke());
    }

    // target="_blank" / window.open from the UI: open the URL in the system browser and stop
    // WebView2 from spawning its own window. Restricted to http/https so non-web schemes are
    // never launched. Best-effort: a failed Process.Start must not bubble out of the WebView.
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true; // never let WebView2 open a popup window
        try
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
                return;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return;
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { /* no browser / blocked: swallow, do not crash the host */ }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? msg;
        try { msg = e.TryGetWebMessageAsString(); }
        catch { msg = e.WebMessageAsJson; }
        if (!string.IsNullOrEmpty(msg)) Received?.Invoke(msg);
    }

    private void UpdateBounds()
    {
        if (_controller == null) return;

        // WebView2 Bounds are in PHYSICAL pixels, but Avalonia's Bounds are logical
        // (device-independent). Multiply by the render scale so the control fills the whole
        // HWND (no uncovered gaps) and the CSS viewport equals the window's logical size.
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var b = Bounds;
        _controller.Bounds = new System.Drawing.Rectangle(
            0, 0,
            Math.Max(0, (int)Math.Round(b.Width * scale)),
            Math.Max(0, (int)Math.Round(b.Height * scale)));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        UpdateBounds();
        return result;
    }

    private static string DefaultUiRoot()
    {
        var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                     ?? Directory.GetCurrentDirectory();
        return Path.Combine(exeDir, "Assets", "ui");
    }
}
#endif
