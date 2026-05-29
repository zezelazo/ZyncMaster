using System;

namespace ZyncMaster.Core;

public sealed class SettingsLoadException : Exception
{
    public SettingsLoadException(string message) : base(message)
    {
    }

    public SettingsLoadException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
