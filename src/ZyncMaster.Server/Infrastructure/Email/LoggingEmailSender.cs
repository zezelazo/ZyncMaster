using Microsoft.Extensions.Logging;

namespace ZyncMaster.Server.Infrastructure.Email;

// Default IEmailSender for dev/tests: logs the message and sends nothing over the wire. The
// real transport (SendGrid) is swapped in Program.cs for production — plan deferred §4. Keeping
// a no-op default means the magic-link flow is fully exercisable locally and in the test suite
// without any network credentials.
public sealed class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;

    public LoggingEmailSender(ILogger<LoggingEmailSender> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
    {
        // NEVER log htmlBody: it contains the clear magic-link token, and logging it (even at
        // Debug) leaks a usable sign-in credential into shipped log sinks.
        // FIX 5 — the recipient address is PII; log only a MASKED form (first char + domain) so the
        // log stays useful for "did a link go out?" without recording the full address.
        _logger.LogInformation(
            "Email suppressed (LoggingEmailSender): to={To} subject={Subject}", MaskEmail(toEmail), subject);
        return Task.CompletedTask;
    }

    // Masks an email for logging: keeps the first character of the local part and the full domain
    // ("alice@example.com" -> "a***@example.com"), so a log reader can recognise the domain / a user
    // they already know without the log exposing the full address. Falls back to a fully-masked
    // token for malformed/blank input so a weird value never leaks verbatim.
    internal static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return "***";

        var at = email.IndexOf('@');
        if (at <= 0 || at == email.Length - 1)
            return "***"; // no usable local-part/domain split — do not risk leaking the raw value.

        var local = email[..at];
        var domain = email[(at + 1)..];
        return $"{local[0]}***@{domain}";
    }
}
