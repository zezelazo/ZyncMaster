using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.App.Bridge;

namespace ZyncMaster.App.Windows;

// Loopback web host — the WebView fallback.
//
// WHY a fallback instead of an embedded WebView: on Avalonia 12 / net10 there is no
// first-party WebView. The only NuGet that restored and built was WebViewControl-Avalonia
// (CEF/CefGlue 120), but it drags in a mixed Avalonia 11 transitive (Avalonia.ReactiveUI
// 11.0.9) alongside the Avalonia 12 core plus a full Chromium native runtime that cannot
// be validated on a headless build agent. Per the project's anti-spin guidance we ship a
// robust, guaranteed-buildable host instead: a loopback HttpListener that serves the
// bundled Assets/ui site and exposes a tiny bridge channel.
//
// This host doubles as the IBridgeTransport surface:
//   GET  /                 -> index.html (and other static assets)
//   POST /__bridge/send    -> inbound message from web -> raised on Received
//   GET  /__bridge/poll    -> long-poll; returns the next queued native->web message
//
// To wire a real WebView later, implement IWebHost over WebView2/WKWebView and route
// its postMessage/window.chrome.webview channel into the same UiBridge — nothing else
// in the app changes.
public sealed class WebHost : IWebHost, IBridgeTransport, IDisposable
{
    private readonly string _uiRoot;
    private readonly HttpListener _listener = new();
    private readonly BlockingCollection<string> _toWeb = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _serveTask;
    private string _baseUrl = "";

    public WebHost(string? uiRoot = null)
    {
        _uiRoot = uiRoot ?? DefaultUiRoot();
    }

    // The loopback URL the UI is served from once Load has started the listener.
    public string BaseUrl => _baseUrl;

    public event Action<string>? Received;

    public void Load()
    {
        if (_serveTask != null)
            return; // already serving

        var port = FindFreeLoopbackPort();
        _baseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_baseUrl);
        _listener.Start();
        _serveTask = Task.Run(() => ServeLoopAsync(_cts.Token));
    }

    public void Reload()
    {
        // Static files are read fresh on each request, so a reload is a no-op server
        // side; a real WebView implementation would call its Reload() here.
    }

    public void PostToWeb(string json)
    {
        if (json == null) throw new ArgumentNullException(nameof(json));
        if (!_toWeb.IsAddingCompleted)
            _toWeb.Add(json);
    }

    // No embedded surface here — the UI renders in an external browser, so there is no in-process
    // web content to move keyboard focus into.
    public void FocusContent() { }

    // IBridgeTransport.Send is the same channel as PostToWeb: native -> web.
    public void Send(string json) => PostToWeb(json);

    private async Task ServeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleRequest(context, ct), ct);
        }
    }

    private async Task HandleRequest(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";

            if (path == "/__bridge/send" && context.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(ct);
                Received?.Invoke(body);
                WriteText(context, 200, "{\"ok\":true}", "application/json");
                return;
            }

            if (path == "/__bridge/poll" && context.Request.HttpMethod == "GET")
            {
                // Block until a native->web message is available or the host shuts down.
                if (_toWeb.TryTake(out var msg, TimeSpan.FromSeconds(25)))
                    WriteText(context, 200, msg, "application/json");
                else
                    WriteText(context, 204, "", "application/json"); // no content -> client re-polls
                return;
            }

            ServeStaticFile(context, path);
        }
        catch
        {
            try { context.Response.StatusCode = 500; context.Response.Close(); } catch { /* ignore */ }
        }
    }

    private void ServeStaticFile(HttpListenerContext context, string path)
    {
        var relative = path == "/" ? "index.html" : path.TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(_uiRoot, relative));

        // Guard against path traversal outside the ui root.
        if (!full.StartsWith(Path.GetFullPath(_uiRoot), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            WriteText(context, 404, "Not found", "text/plain");
            return;
        }

        var bytes = File.ReadAllBytes(full);
        context.Response.StatusCode = 200;
        context.Response.ContentType = ContentTypeFor(full);
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    private static void WriteText(HttpListenerContext context, int status, string body, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    private static string ContentTypeFor(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js"   => "text/javascript; charset=utf-8",
            ".css"  => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg"  => "image/svg+xml",
            _       => "application/octet-stream",
        };
    }

    private static int FindFreeLoopbackPort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string DefaultUiRoot()
    {
        var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                     ?? Directory.GetCurrentDirectory();
        return Path.Combine(exeDir, "Assets", "ui");
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { _toWeb.CompleteAdding(); } catch { /* ignore */ }
        try { if (_listener.IsListening) _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
