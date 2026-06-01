using System.Security.Cryptography;

namespace ZyncMaster.Server;

// Device API keys are formatted "keyId.secret" (§A-3). The keyId is a short, PUBLIC,
// non-secret lookup token that is stored UNHASHED in the indexed DeviceRow.KeyId column so a
// single incoming key locates exactly one candidate device (an O(1) index seek) instead of
// scanning every device and running PBKDF2 against each. The secret half is the only part
// that is hashed (PBKDF2) and verified; the keyId carries no authorization power on its own.
//
// Both halves are independent 128-bit random tokens (url-safe base64), so the keyId is
// effectively a random handle — it leaks nothing about the secret and cannot be brute-forced
// into a valid key without also defeating the PBKDF2-protected secret.
public static class ApiKeyGenerator
{
    public const char Separator = '.';

    // A freshly generated key plus its split parts, returned together so the caller stores the
    // public KeyId (indexed) and the hash of the secret without re-parsing the composite string.
    public sealed record GeneratedKey(string ApiKey, string KeyId, string Secret);

    // Generates a new "keyId.secret" key. The KeyId is stored unhashed (indexed lookup); the
    // Secret is hashed with ApiKeyHasher and the hash stored in DeviceRow.ApiKeyHash.
    public static GeneratedKey GenerateKey()
    {
        var keyId = RandomToken();
        var secret = RandomToken();
        return new GeneratedKey($"{keyId}{Separator}{secret}", keyId, secret);
    }

    // Back-compat shim: returns a single opaque LEGACY-format key (NO keyId separator). Production
    // device minting uses GenerateKey() ("keyId.secret"); this overload exists for code/tests that
    // store Hash(fullKey) with a null KeyId and authenticate via the legacy scan path, exercising
    // the §A-3 backward-compat branch in ApiKeyAuthenticationHandler.
    public static string Generate() => RandomToken() + RandomToken();

    // Splits an incoming key into (keyId, secret). Returns false for a legacy key with no
    // separator (those have no public keyId and must fall back to the scan path) or a malformed
    // key. A key with an empty keyId or empty secret half is rejected.
    public static bool TryParse(string apiKey, out string keyId, out string secret)
    {
        keyId = "";
        secret = "";
        if (string.IsNullOrEmpty(apiKey))
            return false;

        var idx = apiKey.IndexOf(Separator);
        if (idx <= 0 || idx >= apiKey.Length - 1)
            return false;

        keyId = apiKey[..idx];
        secret = apiKey[(idx + 1)..];
        return keyId.Length > 0 && secret.Length > 0;
    }

    private static string RandomToken()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
