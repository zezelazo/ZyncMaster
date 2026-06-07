namespace ZyncMaster.Server;

// Per-user clipboard history. ListAsync returns newest-first, capped at MaxItemsPerUser.
public interface IClipboardHistoryStore
{
    Task AppendAsync(ClipboardItem item, CancellationToken ct = default);
    Task<IReadOnlyList<ClipboardItem>> ListAsync(CancellationToken ct = default);
    Task RemoveAsync(string id, CancellationToken ct = default);
}
