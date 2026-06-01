using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ZyncMaster.Server.Configuration;

namespace ZyncMaster.Server.Infrastructure.Email;

// Production IEmailSender backed by the Mailjet Send API v3.1 (POST https://api.mailjet.com/v3.1/send).
// Authentication is HTTP Basic with "apiKey:apiSecret" Base64-encoded in the Authorization header.
// Registered in Program.cs ONLY when both Mailjet ApiKey and ApiSecret are configured;
// otherwise the dev/test default (LoggingEmailSender) stays in place so a no-config run never
// breaks the magic-link flow.
//
// SECURITY: like LoggingEmailSender, this NEVER logs or surfaces the htmlBody — it carries the
// clear magic-link token. On a non-2xx response it throws EmailDeliveryException with the status
// code only; the raw Mailjet response body is never embedded (Mailjet error payloads can reflect
// request fields back, and the request itself carried the token).
public sealed class MailjetEmailSender : IEmailSender
{
    // Mailjet Send API v3.1 endpoint. Fixed; the per-environment values are the credentials and
    // the From identity, not the URL.
    private const string SendEndpoint = "https://api.mailjet.com/v3.1/send";

    private readonly HttpClient _http;
    private readonly MailjetOptions _options;
    private readonly AuthenticationHeaderValue _authHeader;

    public MailjetEmailSender(HttpClient http, IOptions<MailjetOptions> opts)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentNullException.ThrowIfNull(opts);
        _options = opts.Value ?? throw new ArgumentNullException(nameof(opts));

        // Basic apiKey:apiSecret, Base64. Built once: the credentials are fixed for the lifetime
        // of the sender. A sane request timeout guards against a wedged upstream.
        var raw = $"{_options.ApiKey}:{_options.ApiSecret}";
        _authHeader = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        if (_http.Timeout == System.Threading.Timeout.InfiniteTimeSpan || _http.Timeout > TimeSpan.FromSeconds(30))
            _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(toEmail);

        var payload = BuildPayload(toEmail, subject, htmlBody);

        using var request = new HttpRequestMessage(HttpMethod.Post, SendEndpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = _authHeader;

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Drain the body (so the connection can be reused) but NEVER include it: it can echo
            // the request, which carried the magic-link token. Surface the status code only.
            _ = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new EmailDeliveryException(
                $"Mailjet send endpoint returned {(int)response.StatusCode} ({response.StatusCode}).");
        }
    }

    // Builds the Mailjet v3.1 JSON body. Pulled out (and pure) so it is unit-testable without
    // the network: { "Messages": [ { "From": { Email, Name }, "To": [ { Email } ], Subject,
    // HTMLPart } ] }. JsonSerializer handles all escaping of the subject/body.
    internal string BuildPayload(string toEmail, string subject, string htmlBody)
    {
        var message = new
        {
            From = new { Email = _options.FromEmail, Name = _options.FromName },
            To = new[] { new { Email = toEmail } },
            Subject = subject,
            HTMLPart = htmlBody,
        };
        return JsonSerializer.Serialize(new { Messages = new[] { message } });
    }
}
