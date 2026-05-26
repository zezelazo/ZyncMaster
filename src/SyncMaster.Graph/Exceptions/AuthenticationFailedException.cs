using System;

namespace SyncMaster.Graph;

// Raised when token acquisition (silent or interactive) fails in a way that will
// keep failing on subsequent attempts: bad client id, missing/withdrawn consent,
// user cancelled the sign-in prompt, cache corruption, etc. ApplicationRunner
// treats this as fatal and aborts the entire run instead of marking the item as
// a per-item failure (which would log the same error N times).
public sealed class AuthenticationFailedException : Exception
{
    public AuthenticationFailedException(string message) : base(message) { }
    public AuthenticationFailedException(string message, Exception inner) : base(message, inner) { }
}
