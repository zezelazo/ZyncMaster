namespace ZyncMaster.Graph;

// A single per-item failure from a mirror run, carrying both a human-readable message
// and the typed SyncErrorKind that classifies it. Replaces the old bare string so the
// conditional sweep can ask "did any item fail transiently?" without parsing text.
public sealed record MirrorFailure
{
    public required SyncErrorKind Kind { get; init; }
    public required string Message { get; init; }

    public override string ToString() => $"[{Kind}] {Message}";
}
