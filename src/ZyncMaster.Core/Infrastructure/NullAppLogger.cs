using System;

namespace ZyncMaster.Core;

// No-op logger used by tests and by code paths that have no configured engine (e.g. the App's
// UnconfiguredEngineActions). IsEnabled always returns false so callers skip building verbose
// messages entirely. A process-wide singleton because it holds no state.
public sealed class NullAppLogger : IAppLogger
{
    public static readonly NullAppLogger Instance = new();

    private NullAppLogger() { }

    public void Log(LogLevel level, string message, Exception? ex = null) { }

    public bool IsEnabled(LogLevel level) => false;
}
