using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SyncMaster.Core;

namespace SyncMaster.Engine;

public interface ICalendarSource
{
    Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
}
