using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.Engine;

namespace ZyncMaster.App.Platform.Clipboard;

// Persists the clipboard module's E2E key material on disk, mirroring DpapiDeviceKeyStore /
// FileIdentityTokenCache:
//   * the shared symmetric TEXT key (AES-256, 32 bytes), and
//   * the per-device RSA keypair (PKCS#8 private key) used to relay the text key to a new device.
//
// On Windows both blobs are encrypted at rest with DPAPI scoped to the current user before they
// touch disk; off-Windows DPAPI is unavailable so the bytes are stored base64-encoded only — NOT
// encrypted at rest. The App targets Windows today, so the fallback is the documented best-effort
// path for dev on other OSes.
//
// Default location:
//   %LOCALAPPDATA%\ZyncMaster\App\clipboard.key         (shared text key)
//   %LOCALAPPDATA%\ZyncMaster\App\clipboard-device.key  (RSA private key, PKCS#8)
public sealed class DpapiClipboardKeyStore : IClipboardKeyStore
{
    // HARDENING: the relay keypair is RSA-3072 (>= 3072-bit) so wrapping the 32-byte text key with
    // RSA-OAEP/SHA-256 has a comfortable margin and meets modern key-strength guidance.
    private const int RsaKeySizeBits = 3072;

    private readonly string _textKeyPath;
    private readonly string _devicePrivateKeyPath;

    // Cached so repeated EnsureDeviceKeypairAsync calls hand back the same live RSA instance within a
    // process run rather than re-importing the PKCS#8 blob each time.
    private RSA? _cachedRsa;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DpapiClipboardKeyStore(string textKeyPath, string devicePrivateKeyPath)
    {
        _textKeyPath = textKeyPath ?? throw new ArgumentNullException(nameof(textKeyPath));
        _devicePrivateKeyPath = devicePrivateKeyPath ?? throw new ArgumentNullException(nameof(devicePrivateKeyPath));
    }

    // Builds the store at the canonical %LOCALAPPDATA%\ZyncMaster\App paths.
    public static DpapiClipboardKeyStore CreateDefault()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZyncMaster", "App");
        return new DpapiClipboardKeyStore(
            Path.Combine(baseDir, "clipboard.key"),
            Path.Combine(baseDir, "clipboard-device.key"));
    }

    public async Task<byte[]?> LoadTextKeyAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_textKeyPath))
            return null;

        var bytes = await File.ReadAllBytesAsync(_textKeyPath, ct).ConfigureAwait(false);
        if (bytes.Length == 0)
            return null;

        try
        {
            return Unprotect(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // A corrupt / foreign-user blob is treated as "no key" rather than crashing the App;
            // the device simply re-admits the text key via the relay flow.
            return null;
        }
    }

    public async Task SaveTextKeyAsync(byte[] key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        EnsureDirectory(_textKeyPath);
        var toWrite = Protect(key);
        await File.WriteAllBytesAsync(_textKeyPath, toWrite, ct).ConfigureAwait(false);
    }

    public async Task<(byte[] publicKey, RSA privateKey)> EnsureDeviceKeypairAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedRsa is not null)
                return (_cachedRsa.ExportSubjectPublicKeyInfo(), _cachedRsa);

            if (File.Exists(_devicePrivateKeyPath))
            {
                var stored = await File.ReadAllBytesAsync(_devicePrivateKeyPath, ct).ConfigureAwait(false);
                if (stored.Length > 0)
                {
                    try
                    {
                        var pkcs8 = Unprotect(stored);
                        var rsa = RSA.Create();
                        rsa.ImportPkcs8PrivateKey(pkcs8, out _);
                        CryptographicOperations.ZeroMemory(pkcs8);
                        _cachedRsa = rsa;
                        return (rsa.ExportSubjectPublicKeyInfo(), rsa);
                    }
                    catch (Exception ex) when (ex is CryptographicException or FormatException)
                    {
                        // Unreadable/foreign blob: fall through and generate a fresh keypair.
                    }
                }
            }

            // Generate ONCE and persist the PKCS#8 private key DPAPI-protected for reuse.
            var fresh = RSA.Create(RsaKeySizeBits);
            var exported = fresh.ExportPkcs8PrivateKey();
            try
            {
                EnsureDirectory(_devicePrivateKeyPath);
                await File.WriteAllBytesAsync(_devicePrivateKeyPath, Protect(exported), ct).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(exported);
            }

            _cachedRsa = fresh;
            return (fresh.ExportSubjectPublicKeyInfo(), fresh);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void EnsureDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    private static byte[] Protect(byte[] plain)
    {
        if (OperatingSystem.IsWindows())
            return ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

        // Not encrypted at rest off-Windows: DPAPI is Windows-only.
        return System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(plain));
    }

    private static byte[] Unprotect(byte[] stored)
    {
        if (OperatingSystem.IsWindows())
            return ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser);

        return Convert.FromBase64String(System.Text.Encoding.UTF8.GetString(stored));
    }
}
