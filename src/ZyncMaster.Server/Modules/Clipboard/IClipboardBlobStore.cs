namespace ZyncMaster.Server;

// Stores the heavy bytes of a clipboard item (an image or a file) OUTSIDE the database, keyed by the
// item id and scoped to the owning user. The metadata row (id, type, size, thumbnail, name) stays
// small in Postgres; the blob is fetched lazily over GET /api/clipboard/blobs/{id} only when a device
// actually pastes it — so the history list and the broadcast never move hundreds of MB. Text never
// uses this: its (small) E2E ciphertext stays inline on the row.
public interface IClipboardBlobStore
{
    // Persists the blob for (userId, id), overwriting any prior content. The stream is consumed fully.
    Task SaveAsync(string userId, string id, Stream content, CancellationToken ct = default);

    // Opens the blob for reading, or null when it does not exist (e.g. evicted by retention, or the
    // upload never completed). The caller disposes the stream.
    Task<Stream?> OpenReadAsync(string userId, string id, CancellationToken ct = default);

    // Removes the blob for (userId, id). A missing blob is a no-op (idempotent) — retention and the
    // per-item DELETE both call this without first checking existence.
    Task DeleteAsync(string userId, string id, CancellationToken ct = default);
}
