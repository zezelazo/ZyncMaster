using System;

namespace SyncMaster.CalImport;

public sealed class SettingsValidationException : Exception
{
    public SettingsValidationException(string message) : base(message) { }

    public SettingsValidationException(string message, Exception innerException) : base(message, innerException) { }
}
