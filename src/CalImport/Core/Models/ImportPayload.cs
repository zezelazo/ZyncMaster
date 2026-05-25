using System;
using System.Collections.Generic;
using SyncMaster.Core;

namespace SyncMaster.CalImport;

public sealed class ImportPayload
{
    public DateTimeOffset                ExportedAt { get; init; }
    public int                           Year       { get; init; }
    public int                           Month      { get; init; }
    public string                        MonthName  { get; init; } = "";
    public IReadOnlyList<string>         Calendars  { get; init; } = Array.Empty<string>();
    public IReadOnlyList<AppointmentRecord> Events  { get; init; } = Array.Empty<AppointmentRecord>();
}
