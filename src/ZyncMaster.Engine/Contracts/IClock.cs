using System;

namespace ZyncMaster.Engine;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
