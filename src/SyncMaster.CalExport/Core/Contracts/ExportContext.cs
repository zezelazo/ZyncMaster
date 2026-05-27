using System;
using System.Collections.Generic;

namespace SyncMaster.CalExport;

public sealed class ExportContext
{
    public int                       Year                { get; init; }
    public int                       Month               { get; init; }
    public string                    MonthName           { get; init; } = "";
    public IReadOnlyList<string>     CalendarDisplayNames { get; init; } = Array.Empty<string>();
    public DateTimeOffset            ExportedAt          { get; init; }
}
