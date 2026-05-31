using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.App.Bridge;

namespace ZyncMaster.App.Platform;

// DPAPI-encrypted file store for the identity token pair, mirroring DpapiDeviceKeyStore. The
// {accessToken, refreshToken} pair is serialized to JSON and, on Windows, protected with DPAPI
// scoped to the current user before it touches disk; off-Windows DPAPI is unavailable so the
// JSON is base64-encoded only (NOT encrypted at rest) — the App targets Windows today, so this
// is the documented best-effort fallback.
//
// Default location: %LOCALAPPDATA%\ZyncMaster\App\identity.token.
public sealed class FileIdentityTokenCache : IIdentityTokenCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _storePath;

    public FileIdentityTokenCache(string storePath)
    {
        _storePath = storePath ?? throw new ArgumentNullException(nameof(storePath));
    }

    // Builds the cache at the canonical %LOCALAPPDATA%\ZyncMaster\App\identity.token path.
    public static FileIdentityTokenCache CreateDefault()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZyncMaster", "App", "identity.token");
        return new FileIdentityTokenCache(path);
    }

    public async Task SaveAsync(IdentityTokens tokens, CancellationToken ct = default)
    {
        if (tokens == null) throw new ArgumentNullException(nameof(tokens));

        var dir = Path.GetDirectoryName(_storePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var plain = JsonSerializer.SerializeToUtf8Bytes(tokens, JsonOptions);

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

    public async Task<IdentityTokens?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_storePath))
            return null;

        var bytes = await File.ReadAllBytesAsync(_storePath, ct);
        if (bytes.Length == 0)
            return null;

        byte[] plain;
        try
        {
            if (OperatingSystem.IsWindows())
                plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            else
                plain = Convert.FromBase64String(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // A corrupt / foreign-user blob is treated as "no token" rather than crashing the App;
            // the user simply has to sign in again.
            return null;
        }

        try
        {
            var tokens = JsonSerializer.Deserialize<IdentityTokens>(plain, JsonOptions);
            if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken) || string.IsNullOrEmpty(tokens.RefreshToken))
                return null;
            return tokens;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        if (File.Exists(_storePath))
            File.Delete(_storePath);
        return Task.CompletedTask;
    }
}
