using System;
using System.Collections.Generic;

namespace SyncMaster.Engine;

public sealed record EngineSettings
{
    public string ServerBaseUrl { get; init; } = "";
    public string DeviceName { get; init; } = Environment.MachineName;
    public int SyncWindowDays { get; init; } = 14;
    public int IntervalMinutes { get; init; } = 10;
    public string CalExportPath { get; init; } = "";
    public IReadOnlyList<string>? CalendarNames { get; init; }
}
