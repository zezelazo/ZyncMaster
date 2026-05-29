using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

// Persists the device API key to disk. On Windows the bytes are encrypted at rest
// with DPAPI scoped to the current user; off-Windows DPAPI is unavailable so the
// bytes are stored base64-encoded only — NOT encrypted at rest off-Windows.
public sealed class DpapiDeviceKeyStore : IDeviceKeyStore
{
    private readonly string _storePath;
    private readonly IClock _clock;

    public DpapiDeviceKeyStore(string storePath, IClock clock)
    {
        _storePath = storePath ?? throw new ArgumentNullException(nameof(storePath));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task SaveAsync(string apiKey, CancellationToken ct)
    {
        if (apiKey == null) throw new ArgumentNullException(nameof(apiKey));

        var dir = Path.GetDirectoryName(_storePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var plain = Encoding.UTF8.GetBytes(apiKey);

        byte[] toWrite;
        if (OperatingSystem.IsWindows())
        {
            toWrite = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        }
        else
        {
            // Not encrypted at rest off-Windows: DPAPI is Windows-only.
            toWrite = Encoding.UTF8.GetBytes(Convert.ToBase64String(plain));
        }

        await File.WriteAllBytesAsync(_storePath, toWrite, ct);
    }

    public async Task<string?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_storePath))
            return null;

        var bytes = await File.ReadAllBytesAsync(_storePath, ct);

        byte[] plain;
        if (OperatingSystem.IsWindows())
        {
            plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        }
        else
        {
            plain = Convert.FromBase64String(Encoding.UTF8.GetString(bytes));
        }

        return Encoding.UTF8.GetString(plain);
    }

    public Task ClearAsync(CancellationToken ct)
    {
        if (File.Exists(_storePath))
            File.Delete(_storePath);
        return Task.CompletedTask;
    }
}
