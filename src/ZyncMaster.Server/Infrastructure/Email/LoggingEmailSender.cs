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
        // Body is logged at Debug only — it carries the clear magic-link token, so it must not
        // appear at Information level where it could land in shipped logs.
        _logger.LogInformation("Email suppressed (LoggingEmailSender): to={To} subject={Subject}", toEmail, subject);
        _logger.LogDebug("Email body for {To}: {Body}", toEmail, htmlBody);
        return Task.CompletedTask;
    }
}
