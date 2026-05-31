namespace ZyncMaster.Server.Infrastructure.Email;

// Outbound transactional email seam. The magic-link flow depends only on this abstraction so
// the transport (SendGrid in production, a no-op logger in dev/tests) is a one-line swap in
// Program.cs. The real SendGrid implementation (free tier 100/day, behind an API key from
// configuration/user-secrets) is a later integration task — plan deferred §4. Until then the
// registered implementation is LoggingEmailSender, which logs the message and sends nothing.
public interface IEmailSender
{
    // Sends an HTML email. Implementations MUST NOT throw on a recoverable transport hiccup in
    // a way that leaks recipient existence to the caller of /identity/magic-link — the endpoint
    // returns a constant 202 regardless (plan A-6 anti-enumeration).
    Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default);
}
