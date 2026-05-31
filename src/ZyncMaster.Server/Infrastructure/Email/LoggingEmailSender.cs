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
        // Debug) leaks a usable sign-in credential into shipped log sinks. Only the recipient and
        // subject are logged — neither carries the token.
        _logger.LogInformation("Email suppressed (LoggingEmailSender): to={To} subject={Subject}", toEmail, subject);
        return Task.CompletedTask;
    }
}
