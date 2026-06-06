using System;
using System.Collections.Generic;

namespace ZyncMaster.Engine;

public sealed record EngineSettings
{
    public string ServerBaseUrl { get; init; } = "";
    public string DeviceName { get; init; } = Environment.MachineName;
    public int SyncWindowDays { get; init; } = 14;
    public int IntervalMinutes { get; init; } = 10;
    public string CalExportPath { get; init; } = "";

    // Hard cap on how long a single headless CalExport child process may run before it is killed.
    // Outlook can pop a modal dialog (Programmatic Access "Allow access", a corrupt profile prompt,
    // an MFA wall) that blocks the child indefinitely, wedging the scheduler and leaking an orphan
    // process. The runner enforces this ceiling and kills the whole process tree on timeout. A
    // non-positive value falls back to the runner's default (CalExportRunner.DefaultTimeoutMinutes).
    public int CalExportTimeoutMinutes { get; init; } = 5;

    public IReadOnlyList<string>? CalendarNames { get; init; }
}
