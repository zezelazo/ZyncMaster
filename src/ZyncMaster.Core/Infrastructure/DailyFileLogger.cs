using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace ZyncMaster.Core;

// Thread-safe local logger that appends one line per entry to a per-day file:
//
//   %LOCALAPPDATA%\ZyncMaster\logs\zyncmaster-YYYY-MM-DD.log
//
// The day component is recomputed on every write from the injected clock, so a process that
// runs across midnight rolls over to a new file automatically (no restart needed). The log
// directory is created on construction.
//
// Multiple device-side threads write through a single instance (PairScheduler tick,
// DeviceHeartbeatLoop, the App's status loop, "Sync now"), so every write is serialised under
// a private lock. The file is opened/closed per write (append): the volume is low and this keeps
// the day-rollover trivial and avoids holding an OS handle across an idle App's whole lifetime.
//
// The clock is injected as a Func<DateTimeOffset> rather than the Engine's IClock so this stays
// in ZyncMaster.Core (which must not reference ZyncMaster.Engine). Callers that already have an
// IClock pass `() => clock.UtcNow`; CalExport (no Engine reference) passes `() => DateTimeOffset.UtcNow`.
public sealed class DailyFileLogger : IAppLogger
{
    private readonly string _logDirectory;
    private readonly LogLevel _minLevel;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate = new();

    public DailyFileLogger(string logDirectory, LogLevel minLevel, Func<DateTimeOffset> utcNow)
    {
        _logDirectory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _minLevel = minLevel;

        Directory.CreateDirectory(_logDirectory);
    }

    // Resolves the default log directory: %LOCALAPPDATA%\ZyncMaster\logs. Shared by every
    // composition root so all four tools (and the CalExport child process) write to the same
    // per-day file.
    public static string DefaultLogDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZyncMaster", "logs");

    // The absolute path of the log file for the current day, recomputed from the clock so a
    // cross-midnight write lands in the new day's file. Exposed so each composition root can log
    // the path at startup ("Logging to ...").
    public string CurrentLogFilePath()
        => Path.Combine(_logDirectory, FileNameFor(_utcNow()));

    public bool IsEnabled(LogLevel level) => level >= _minLevel;

    public void Log(LogLevel level, string message, Exception? ex = null)
    {
        if (level < _minLevel)
            return;

        var now = _utcNow();
        var line = Format(now, level, message ?? string.Empty, ex);
        var path = Path.Combine(_logDirectory, FileNameFor(now));

        lock (_gate)
        {
            try
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must never take down the caller: a locked/unavailable log file is
                // swallowed. The whole point of this logger is diagnostics, not a failure surface.
            }
        }
    }

    private static string FileNameFor(DateTimeOffset when)
        => "zyncmaster-" + when.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log";

    private static string Format(DateTimeOffset when, LogLevel level, string message, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append(when.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        sb.Append(" [").Append(level).Append("] ");
        sb.Append(message);
        sb.Append(Environment.NewLine);
        if (ex != null)
        {
            sb.Append(ex);
            sb.Append(Environment.NewLine);
        }
        return sb.ToString();
    }
}
