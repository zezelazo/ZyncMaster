namespace ZyncMaster.Server.Infrastructure.Email;

// Thrown when an outbound transactional email could not be delivered by the transport. The
// message carries ONLY non-secret diagnostics (a status code / short reason) — never the
// htmlBody (which holds the clear magic-link token) nor the raw upstream response body (which
// can reflect request fields back). Mirrors the AuthenticationFailedException discipline used
// by MicrosoftTokenService.
public sealed class EmailDeliveryException : Exception
{
    public EmailDeliveryException(string message) : base(message) { }

    public EmailDeliveryException(string message, Exception inner) : base(message, inner) { }
}
