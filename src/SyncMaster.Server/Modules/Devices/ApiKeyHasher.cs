using System.Security.Cryptography;

namespace SyncMaster.Server;

public static class ApiKeyHasher
{
    private const int Iterations = 100_000, SaltLen = 16, HashLen = 32;

    public static string Hash(string key)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var hash = Rfc2898DeriveBytes.Pbkdf2(key, salt, Iterations, HashAlgorithmName.SHA256, HashLen);
        return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string key, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 4 || parts[0] != "v1") return false;
        if (!int.TryParse(parts[1], out var iter)) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(key, salt, iter, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
