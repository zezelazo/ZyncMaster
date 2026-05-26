#if WIN_WEBVIEW2
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
using SyncMaster.App.Bridge;

namespace SyncMaster.App.Windows;

// Embedded WebView2 host (Windows only — compiled into the net10.0-windows target).
//
// It is a NativeControlHost: Avalonia hands it a child HWND, onto which we attach a
// CoreWebView2 controller. The bundled Assets/ui site is served through a virtual host
// mapping (https://syncmaster.assets/...), and the UI's window.chrome.webview channel is
// bridged to the app's IBridgeTransport — WebView2 always injects window.chrome.webview,
// so the UI's native-bridge branch (postMessage / 'message' events) is what runs here.
//
// macOS will get a WKWebView-backed IWebHost behind this same seam; nothing else changes.
public sealed class WebView2WebHost : NativeControlHost, IWebHost, IBridgeTransport
{
    private const string VirtualHost = "syncmaster.assets";
    private const string StartUrl = "https://syncmaster.assets/index.html";

    private readonly string _uiRoot;
    private readonly Queue<string> _pending = new();
    private CoreWebView2Controller? _controller;
    private bool _ready;

    public event Action<string>? Received;

    public WebView2WebHost(string? uiRoot = null)
    {
        _uiRoot = uiRoot ?? DefaultUiRoot();
    }

    // Navigation is driven by the controller becoming ready (see InitAsync) once the
    // control is attached to the window. If already ready, (re)load the start page.
    public void Load()
    {
        if (_ready) _controller!.CoreWebView2.Navigate(StartUrl);
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
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SyncMaster", "App", "WebView2");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            _controller = await env.CreateCoreWebView2ControllerAsync(hwnd);

            UpdateBounds();

            var core = _controller.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, _uiRoot, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessageReceived;
            core.Navigate(StartUrl);

            _ready = true;
            while (_pending.Count > 0)
                core.PostWebMessageAsString(_pending.Dequeue());
        }
        catch
        {
            // WebView2 runtime missing or init failed: leave the host blank rather than
            // crash. The tray remains usable and the engine keeps running headless.
            _ready = false;
        }
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
