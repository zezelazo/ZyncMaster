using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SyncMaster.Engine;

namespace SyncMaster.App.Platform;

// Plain-file fallback key store for platforms that are neither Windows (DPAPI) nor
// macOS (Keychain) — primarily Linux. The key is base64-encoded but NOT encrypted at
// rest; on Linux there is no single ubiquitous user-scoped secret store to rely on
// without extra dependencies, so this is a clearly-documented best-effort fallback.
public sealed class FileDeviceKeyStore : IDeviceKeyStore
{
    private readonly string _storePath;

    public FileDeviceKeyStore(string storePath)
    {
        _storePath = storePath ?? throw new ArgumentNullException(nameof(storePath));
    }

    public async Task SaveAsync(string apiKey, CancellationToken ct)
    {
        if (apiKey == null) throw new ArgumentNullException(nameof(apiKey));

        var dir = Path.GetDirectoryName(_storePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(apiKey));
        await File.WriteAllTextAsync(_storePath, encoded, ct);
    }

    public async Task<string?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_storePath))
            return null;

        var encoded = await File.ReadAllTextAsync(_storePath, ct);
        if (string.IsNullOrWhiteSpace(encoded))
            return null;

        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Trim()));
    }

    public Task ClearAsync(CancellationToken ct)
    {
        if (File.Exists(_storePath))
            File.Delete(_storePath);
        return Task.CompletedTask;
    }
}
