namespace ZyncMaster.Server;

// Per-user clipboard history. ListAsync returns newest-first, capped at MaxItemsPerUser.
// GetNewestAsync returns the user's single newest item (null when the history is empty) so the
// publish endpoint can dedupe an incoming item against the head without materialising the list.
public interface IClipboardHistoryStore
{
    Task AppendAsync(ClipboardItem item, CancellationToken ct = default);
    Task<IReadOnlyList<ClipboardItem>> ListAsync(CancellationToken ct = default);
    Task<ClipboardItem?> GetNewestAsync(CancellationToken ct = default);
    Task RemoveAsync(string id, CancellationToken ct = default);
}
