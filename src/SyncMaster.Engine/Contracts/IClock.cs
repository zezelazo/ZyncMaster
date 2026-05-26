using System;

namespace SyncMaster.Engine;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
