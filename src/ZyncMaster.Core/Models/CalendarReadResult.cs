using System.Collections.Generic;

namespace ZyncMaster.Core;

public sealed record CalendarReadResult
{
    public required IReadOnlyList<AppointmentRecord> Events { get; init; }
    public string? PeriodLabel { get; init; }
}
