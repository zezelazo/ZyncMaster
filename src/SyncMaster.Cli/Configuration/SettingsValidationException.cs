using System;

namespace SyncMaster.Cli;

// Raised when settings load but contain values that cannot be used (e.g. a missing
// required field). Mirrors CalImport's SettingsValidationException — each tool owns
// its own so the message wording stays tool-specific.
public sealed class SettingsValidationException : Exception
{
    public SettingsValidationException(string message) : base(message) { }

    public SettingsValidationException(string message, Exception innerException) : base(message, innerException) { }
}
