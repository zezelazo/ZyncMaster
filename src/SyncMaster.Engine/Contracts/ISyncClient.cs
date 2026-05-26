using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SyncMaster.Core;

namespace SyncMaster.Engine;

public interface ISyncClient
{
    Task<SyncPushResult> PushAsync(string apiKey, IReadOnlyList<AppointmentRecord> events, CancellationToken ct);
}
