using System.Security.Cryptography;
using System.Text;

namespace ZyncMaster.Server;

// FIX 1 — PKCE primitive for the anonymous device-pairing flow. /api/pair/start mints a clear
// verifier and stores only its SHA-256; /api/pair/complete must present a verifier whose hash
// matches before the one-time api key is released. This binds completion to whoever initiated the
// pairing, closing the device-code account-takeover (an attacker who learns a victim's pairingId
// cannot complete the handshake without the verifier).
public static class PairingVerifier
{
    // 256 bits of entropy, base64url. Far beyond brute-force; the verifier is the secret half of
    // the handshake, so it is generated like the magic-link/refresh tokens elsewhere in the server.
    public static string Generate() => ToBase64Url(RandomNumberGenerator.GetBytes(32));

    // SHA-256(verifier), base64url — the only form persisted on the pending row. SHA-256 is
    // appropriate here (not PBKDF2) because the input is a 256-bit random secret, not a low-entropy
    // user password, so it cannot be brute-forced from the hash.
    public static string Hash(string verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        return ToBase64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
    }

    // Constant-time comparison of Hash(presented) against the stored hash, so a timing side-channel
    // cannot reveal the hash one byte at a time. Returns false on any null/blank input.
    public static bool Matches(string? presentedVerifier, string? storedHash)
    {
        if (string.IsNullOrEmpty(presentedVerifier) || string.IsNullOrEmpty(storedHash))
            return false;

        var computed = Encoding.UTF8.GetBytes(Hash(presentedVerifier));
        var stored = Encoding.UTF8.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(computed, stored);
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
