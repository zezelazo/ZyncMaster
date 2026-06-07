using System;

namespace ZyncMaster.Engine;

public sealed class SyncClientException : Exception
{
    // HTTP status code of the failed response, when the failure was a non-2xx (null for a transport
    // error with no response). Lets callers react to specific codes — e.g. a 401 on a device-key call
    // means the stored key is stale/invalid and the device must clear it and re-register.
    public int? StatusCode { get; }

    public SyncClientException(string message) : base(message) { }
    public SyncClientException(string message, Exception inner) : base(message, inner) { }
    public SyncClientException(string message, int statusCode) : base(message) => StatusCode = statusCode;
}
