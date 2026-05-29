using System;

namespace ZyncMaster.App;

// Raised when settings load but contain values that cannot be used (e.g. a missing
// required field). Mirrors CalImport's / the Cli's SettingsValidationException — each
// tool owns its own so the message wording stays tool-specific. Load failures (a file
// that exists but cannot be deserialized) surface as Core's SettingsLoadException.
public sealed class SettingsValidationException : Exception
{
    public SettingsValidationException(string message) : base(message) { }

    public SettingsValidationException(string message, Exception innerException) : base(message, innerException) { }
}
