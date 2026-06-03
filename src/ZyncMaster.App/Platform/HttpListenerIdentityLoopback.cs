using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.App.Bridge;

namespace ZyncMaster.App.Platform;

// HttpListener-backed loopback for the identity sign-in flow (plan v2 §A / finding I1). It binds
// an OS-assigned ephemeral port on 127.0.0.1 so no fixed port has to be reserved or firewalled,
// then awaits the single browser redirect the Server sends after sign-in. The port is read by the
// caller and signed into the OAuth state so the Server knows where to redirect back.
//
// This is untested infrastructure (a process/OS boundary like DefaultBrowserLauncher and
// OutlookCalendarService, per CLAUDE.md). The orchestration above it (IdentityLoginService /
// CalendarConnectService) is tested against the IIdentityLoopback interface with a fake.
//
// The callback path is parameterized so the SAME implementation serves both loopback flows: the
// identity sign-in (default /identity/callback/) and the calendar-connect flow (/calendar/callback/,
// where the Server redirects with ?status=connected&nonce=...). Generalizing the concrete listener
// here keeps the untested process boundary in one place instead of duplicating it per flow.
public sealed class HttpListenerIdentityLoopback : IIdentityLoopback, IDisposable
{
    private readonly string _callbackPath;

    private HttpListener? _listener;

    public HttpListenerIdentityLoopback(string callbackPath = "/identity/callback/")
    {
        if (string.IsNullOrWhiteSpace(callbackPath))
            throw new ArgumentNullException(nameof(callbackPath));
        // HttpListener prefixes must end with '/'; normalize so callers can pass either form.
        _callbackPath = callbackPath.EndsWith('/') ? callbackPath : callbackPath + "/";
    }

    public int Port { get; private set; }

    public Task StartAsync(CancellationToken ct = default)
    {
        // Ask the OS for a free ephemeral port via a throwaway TcpListener, then bind HttpListener
        // to it. HttpListener has no "port 0" mode, so this two-step is the standard approach.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}{_callbackPath}");
        listener.Start();
        _listener = listener;
        return Task.CompletedTask;
    }

    public async Task<LoopbackCallback> WaitForCallbackAsync(CancellationToken ct = default)
    {
        if (_listener is null)
            throw new InvalidOperationException("StartAsync must be called before awaiting the callback.");

        // GetContextAsync ignores cancellation tokens, so race it against the token: whichever
        // completes first wins. On cancellation we abort the listener to unblock the pending call.
        using var reg = ct.Register(() => { try { _listener?.Abort(); } catch { /* ignore */ } });

        HttpListenerContext context;
        try
        {
            context = await _listener.GetContextAsync();
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in context.Request.QueryString.AllKeys)
        {
            if (key is not null)
                query[key] = context.Request.QueryString[key] ?? "";
        }

        // Show the user a tiny "you can close this tab" page so the browser does not hang. Kept
        // flow-neutral ("Done") since the same listener serves both sign-in and calendar-connect.
        var html =
            "<!DOCTYPE html><html><body style=\"font-family:sans-serif\">" +
            "<h2>Done</h2>" +
            "<p>You can close this tab and return to Zync Master.</p>" +
            "</body></html>";
        var bytes = System.Text.Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, ct);
        context.Response.OutputStream.Close();

        return new LoopbackCallback(query);
    }

    public void Stop()
    {
        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _listener?.Close(); } catch { /* ignore */ }
        _listener = null;
    }

    public void Dispose() => Stop();
}

// Opens a URL in the default system browser. Untested process boundary, like
// DefaultBrowserLauncher.
public sealed class DefaultSystemBrowser : ISystemBrowser
{
    public void Open(string url)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }
}
