using System;
using System.Collections.Generic;
using ZyncMaster.Core;

namespace ZyncMaster.CalExport;

public sealed class ExportParameters
{
    public int                               Year             { get; }
    public int                               Month            { get; }
    public ExportMode                        Mode             { get; }
    public bool                              IncludeCancelled { get; }
    public IReadOnlyList<CalendarFolderInfo>? SelectedFolders { get; }  // null = all

    public ExportParameters(
        int year,
        int month,
        ExportMode mode,
        bool includeCancelled,
        IReadOnlyList<CalendarFolderInfo>? selectedFolders = null)
    {
        if (year < 1)
            throw new ArgumentOutOfRangeException(nameof(year), $"Year must be positive, but was {year}.");
        if (month < 1 || month > 12)
            throw new ArgumentOutOfRangeException(nameof(month), $"Month must be between 1 and 12, but was {month}.");

        Year             = year;
        Month            = month;
        Mode             = mode;
        IncludeCancelled = includeCancelled;
        SelectedFolders  = selectedFolders;
    }
}
