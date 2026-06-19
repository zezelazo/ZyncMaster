namespace ZyncMaster.Server;

// Filesystem-backed blob store: one file per (userId, id) under a configured root. Blobs are written
// to a .tmp sibling then atomically moved into place, so a crashed/cancelled upload never leaves a
// truncated file a reader would serve as if complete. Keys are GUIDs/hashes, but every key is still
// validated as a single safe path segment so a crafted id can never escape the root.
public sealed class DiskClipboardBlobStore : IClipboardBlobStore
{
    private readonly string _root;

    public DiskClipboardBlobStore(string root)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        Directory.CreateDirectory(_root);
    }

    public async Task SaveAsync(string userId, string id, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = PathFor(userId, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            await content.CopyToAsync(fs, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    public Task<Stream?> OpenReadAsync(string userId, string id, CancellationToken ct = default)
    {
        var path = PathFor(userId, id);
        if (!File.Exists(path))
            return Task.FromResult<Stream?>(null);
        return Task.FromResult<Stream?>(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    public Task DeleteAsync(string userId, string id, CancellationToken ct = default)
    {
        var path = PathFor(userId, id);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort: a locked/missing file is fine — retention sweeps it again next time.
        }
        return Task.CompletedTask;
    }

    private string PathFor(string userId, string id)
    {
        if (!IsSafeSegment(userId) || !IsSafeSegment(id))
            throw new ArgumentException("Unsafe clipboard blob key.");
        return Path.Combine(_root, userId, id);
    }

    // A key is safe only if it is a single path segment with no traversal: no separators, no invalid
    // filename chars, not "." or "..".
    private static bool IsSafeSegment(string s) =>
        !string.IsNullOrEmpty(s)
        && s != "."
        && s != ".."
        && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !s.Contains('/')
        && !s.Contains('\\');
}
