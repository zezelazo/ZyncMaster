using System;

namespace ZyncMaster.Graph;

// Raised for per-item Graph failures that should not abort the entire run:
// transport errors after retries are exhausted, non-auth 4xx (404, 409, 422),
// transient 5xx after retries are exhausted, malformed response payloads, etc.
// ApplicationRunner catches this to record a per-item failure and continue.
//
// IsTransient is a TYPED signal that the underlying cause is retryable (throttling,
// 5xx, request timeout, transport drop, or a malformed/truncated read response).
// SyncErrorClassifier keys off this flag rather than re-parsing the message wording,
// because a retryable failure that is misclassified as non-transient would re-open the
// data-loss path (a partial read would let the destructive sweep run). Every throw that
// represents a retryable condition MUST set IsTransient = true.
public sealed class GraphRequestException : Exception
{
    public bool IsTransient { get; }

    public GraphRequestException(string message, bool isTransient = false) : base(message)
        => IsTransient = isTransient;

    public GraphRequestException(string message, Exception inner, bool isTransient = false) : base(message, inner)
        => IsTransient = isTransient;
}
