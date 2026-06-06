using System;
using System.Globalization;
using System.IO;
using System.Linq;
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
//
// Retention: on construction the logger deletes its own per-day files older than `retentionDays`
// (default 30; <= 0 disables retention). This is a best-effort, one-shot sweep — a locked or
// undeletable file is skipped silently so a full disk or an open handle never takes down startup.
public sealed class DailyFileLogger : IAppLogger
{
    // Default number of days of per-day log files to keep before the startup sweep deletes them.
    public const int DefaultRetentionDays = 30;

    private const string FilePrefix = "zyncmaster-";
    private const string FileSuffix = ".log";

    private readonly string _logDirectory;
    private readonly LogLevel _minLevel;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _gate = new();

    public DailyFileLogger(
        string logDirectory,
        LogLevel minLevel,
        Func<DateTimeOffset> utcNow,
        int retentionDays = DefaultRetentionDays)
    {
        _logDirectory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _minLevel = minLevel;

        Directory.CreateDirectory(_logDirectory);

        PurgeOldLogs(retentionDays);
    }

    // Deletes per-day log files whose date component is older than `retentionDays` relative to the
    // clock's current UTC day. Best-effort: any IO failure (locked file, gone directory, malformed
    // name) is swallowed per-file so retention never throws into the composition root. A value of
    // zero or less disables the sweep entirely.
    private void PurgeOldLogs(int retentionDays)
    {
        if (retentionDays <= 0)
            return;

        DateTime cutoff;
        try
        {
            cutoff = _utcNow().UtcDateTime.Date.AddDays(-retentionDays);
        }
        catch
        {
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(_logDirectory, FilePrefix + "*" + FileSuffix);
        }
        catch
        {
            return;
        }

        foreach (var file in files)
        {
            if (!TryParseFileDate(Path.GetFileName(file), out var fileDate))
                continue;

            if (fileDate >= cutoff)
                continue;

            try
            {
                File.Delete(file);
            }
            catch
            {
                // Best-effort: a locked or vanished file is left in place. Retention is a courtesy,
                // not a guarantee — it must never surface as a startup failure.
            }
        }
    }

    // Parses "zyncmaster-YYYY-MM-DD.log" back to its UTC date. Returns false for any file that does
    // not match the exact pattern this logger writes, so unrelated files in the directory are left
    // untouched.
    private static bool TryParseFileDate(string? fileName, out DateTime date)
    {
        date = default;
        if (fileName is null
            || !fileName.StartsWith(FilePrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(FileSuffix, StringComparison.Ordinal))
            return false;

        var datePart = fileName.Substring(
            FilePrefix.Length,
            fileName.Length - FilePrefix.Length - FileSuffix.Length);

        return DateTime.TryParseExact(
            datePart,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
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
        => FilePrefix + when.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + FileSuffix;

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
