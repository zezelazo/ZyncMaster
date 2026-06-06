using System;

namespace ZyncMaster.Engine;

// Thrown when a headless CalExport child process exceeds its time budget and is killed (process
// tree) by the runner. Distinct from the InvalidOperationException used for a non-zero exit so a
// caller (and the App) can tell "Outlook is wedged on a modal dialog" apart from a normal failure.
public sealed class CalExportTimeoutException : Exception
{
    public CalExportTimeoutException(string message) : base(message) { }

    public CalExportTimeoutException(string message, Exception inner) : base(message, inner) { }
}
