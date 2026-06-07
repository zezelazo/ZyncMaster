namespace ZyncMaster.Server;

// Tunable caps for the per-user clipboard history. Defaults match the product spec:
// at most 100 items per user (FIFO eviction), a 25 MB soft / 50 MB hard ceiling on a
// single image, and a 300 MB rolling budget for all of a user's image payloads combined.
public sealed class ClipboardOptions
{
    public int MaxItemsPerUser { get; set; } = 100;
    public long MaxImageBytes { get; set; } = 25L * 1024 * 1024;
    public long HardMaxImageBytes { get; set; } = 50L * 1024 * 1024;
    public long MaxImageTotalBytesPerUser { get; set; } = 300L * 1024 * 1024;
}

// Thrown by the history store when a single image exceeds the hard ceiling and must be
// rejected outright (rather than silently evicting other entries to make room).
public sealed class ClipboardImageTooLargeException : Exception
{
    public ClipboardImageTooLargeException(long size, long max)
        : base($"Image of {size} bytes exceeds the {max}-byte limit.") { }
}
