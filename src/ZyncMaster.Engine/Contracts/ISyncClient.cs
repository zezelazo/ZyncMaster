using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.Core;

namespace ZyncMaster.Engine;

public interface ISyncClient
{
    Task<SyncPushResult> PushAsync(string apiKey, IReadOnlyList<AppointmentRecord> events, CancellationToken ct);
}
