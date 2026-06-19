namespace ZyncMaster.Server;

// Tunable caps for the per-user clipboard history. Defaults match the product spec:
// at most 100 items per user (FIFO eviction), a 25 MB soft / 50 MB hard ceiling on a
// single image, a 300 MB rolling budget for all of a user's image payloads combined, and
// a 24-hour age window after which an item is dropped on the next append.
public sealed class ClipboardOptions
{
    public int MaxItemsPerUser { get; set; } = 100;
    public long MaxImageBytes { get; set; } = 25L * 1024 * 1024;
    public long HardMaxImageBytes { get; set; } = 50L * 1024 * 1024;
    public long MaxImageTotalBytesPerUser { get; set; } = 300L * 1024 * 1024;

    // Age-based retention: items older than this are evicted on the next append (alongside the FIFO
    // and image-byte caps), so the clipboard stays a short rolling buffer rather than an ever-growing
    // log on disk. TimeSpan.Zero (or negative) disables age eviction. Default 24h.
    public TimeSpan RetentionMaxAge { get; set; } = TimeSpan.FromHours(24);

    // Lazy-blob storage. Image/file bytes live OUTSIDE the DB as files under this root (keyed by
    // userId/itemId); only metadata + thumbnail stay on the row. Empty => a "clipboard-blobs" folder
    // under the server content root. MaxBlobBytes is the hard ceiling for a single uploaded blob (a
    // file/image larger than this is not synced byte-for-byte; the client shows it as a link entry).
    public string BlobRoot { get; set; } = "";
    public long MaxBlobBytes { get; set; } = 100L * 1024 * 1024;
}

// Thrown by the history store when a single image exceeds the hard ceiling and must be
// rejected outright (rather than silently evicting other entries to make room).
public sealed class ClipboardImageTooLargeException : Exception
{
    public ClipboardImageTooLargeException(long size, long max)
        : base($"Image of {size} bytes exceeds the {max}-byte limit.") { }
}
