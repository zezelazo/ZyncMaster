using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ZyncMaster.Server;

public static class PairApprovalEndpoints
{
    public static void MapPairApprovalEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Browser-facing approval page. Gated on a SIGNED-IN user: approval binds the new
        // device to the approver, so we need to know who they are. We keep the route
        // anonymous at the scheme level (so it can issue a friendly redirect instead of a
        // bare 401) and check authentication inside the handler — an unauthenticated visitor
        // is sent through /connect first and returned here afterwards. This page is NOT
        // api-key protected — it is reached from a human's browser; the api key lives only
        // on the paired device.
        app.MapGet("/pair", async (HttpContext context, IDeviceStore devices, IOptions<ServerOptions> opts) =>
        {
            var code = context.Request.Query["code"].ToString();

            // The Cookie scheme is not the default (ApiKey is), so it does not run
            // automatically on this anonymous route. Authenticate it explicitly to detect
            // a signed-in browser session.
            var auth = await context.AuthenticateAsync(AuthSchemes.Cookie);
            if (!auth.Succeeded || auth.Principal?.Identity?.IsAuthenticated != true)
            {
                var returnTo = "/pair" + (string.IsNullOrEmpty(code)
                    ? ""
                    : "?code=" + Uri.EscapeDataString(code));
                return Results.Redirect("/connect?returnTo=" + Uri.EscapeDataString(returnTo));
            }

            if (string.IsNullOrWhiteSpace(code))
                return Results.Content(Page("Pair a device", "<p>No pairing code supplied.</p>"), "text/html");

            // TTL-bounded lookup (FIX A): an expired code resolves to null, so the page shows the
            // same "not valid or has expired" message rather than offering to approve a stale code.
            var ttl = opts.Value.PendingPairingTtlMinutes <= 0 ? 15 : opts.Value.PendingPairingTtlMinutes;
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-ttl);
            var pending = await devices.GetPendingByCodeAsync(code, cutoff, context.RequestAborted);
            if (pending is null)
                return Results.Content(Page("Pair a device",
                    "<p>That pairing code is not valid or has expired.</p>"), "text/html");

            if (pending.Approved)
                return Results.Content(Page("Device approved",
                    $"<p><strong>{Encode(pending.DeviceName)}</strong> is already approved.</p>"), "text/html");

            // The existing approve endpoint consumes JSON, so the button POSTs the code as
            // a JSON body via fetch rather than a classic urlencoded form submit.
            var body =
                $"<p>Approve <strong>{Encode(pending.DeviceName)}</strong> to sync with this account?</p>" +
                $"<p class=\"code\">{Encode(pending.Code)}</p>" +
                $"<button id=\"approve\" type=\"button\" data-code=\"{Encode(pending.Code)}\">Approve</button>" +
                "<p id=\"result\"></p>" +
                "<script>document.getElementById('approve').addEventListener('click',async function(){" +
                "var c=this.getAttribute('data-code');" +
                "var r=await fetch('/api/devices/approve',{method:'POST'," +
                "headers:{'Content-Type':'application/json'},body:JSON.stringify({code:c})});" +
                "document.getElementById('result').textContent=r.ok?'Approved.':'Approval failed.';" +
                "});</script>";

            return Results.Content(Page("Pair a device", body), "text/html");
        });
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string Page(string title, string body) =>
        "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\" />" +
        $"<title>{Encode(title)}</title></head><body>" +
        $"<h1>{Encode(title)}</h1>{body}</body></html>";
}
