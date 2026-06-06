using System;

namespace ZyncMaster.CalExport;

// Thrown when Outlook Classic cannot be reached at all — it is not installed/registered, or the
// COM Application object cannot be created/started. Distinct from a generic failure so Program.cs
// can exit with a DISTINGUISHABLE code (ExitCodes.OutlookUnavailable) and a clean one-line message
// instead of dumping a COM stack trace to stderr, letting the App surface a friendly explanation.
public sealed class OutlookUnavailableException : Exception
{
    public OutlookUnavailableException(string message) : base(message) { }

    public OutlookUnavailableException(string message, Exception inner) : base(message, inner) { }
}
