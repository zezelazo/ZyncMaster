namespace ZyncMaster.Server.Configuration;

// Mailjet transactional email (Send API v3.1). Bound from the dedicated "Mailjet" configuration
// section. When BOTH ApiKey and ApiSecret are set, Program.cs registers MailjetEmailSender as the
// IEmailSender; when either is empty the dev/test default (LoggingEmailSender) stays in place, so a
// no-config run never breaks the magic-link flow. Mailjet is therefore OPTIONAL — it is
// intentionally excluded from the production fail-fast (StartupConfigValidator).
//
// The key/secret are credentials and MUST come from user-secrets in dev (Mailjet:ApiKey,
// Mailjet:ApiSecret) and environment variables / app settings in prod (Mailjet__ApiKey,
// Mailjet__ApiSecret), NEVER committed. Defaults are "".
public sealed class MailjetOptions
{
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";

    // The verified sender identity Mailjet sends from. The From email MUST be a sender/domain you
    // have validated in the Mailjet account, or Mailjet rejects the send. Set per-environment via
    // Mailjet__FromEmail / Mailjet__FromName.
    public string FromEmail { get; set; } = "";
    public string FromName { get; set; } = "Zync Master";
}
