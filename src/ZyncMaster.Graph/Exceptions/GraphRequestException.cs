using System;

namespace ZyncMaster.Graph;

// Raised for per-item Graph failures that should not abort the entire run:
// transport errors after retries are exhausted, non-auth 4xx (404, 409, 422),
// transient 5xx after retries are exhausted, malformed response payloads, etc.
// ApplicationRunner catches this to record a per-item failure and continue.
public sealed class GraphRequestException : Exception
{
    public GraphRequestException(string message) : base(message) { }
    public GraphRequestException(string message, Exception inner) : base(message, inner) { }
}
