using System;

namespace SyncMaster.Engine;

public sealed class SyncClientException : Exception
{
    public SyncClientException(string message) : base(message) { }
    public SyncClientException(string message, Exception inner) : base(message, inner) { }
}
