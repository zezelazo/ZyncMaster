using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ZyncMaster.Server;

// The skip-by-hash key of the replica engine: a stable digest of EXACTLY the fields a replica
// mirrors from its source (start/end/showAs/isAllDay — spec §3). The runner PATCHes a replica
// only when the source's current hash differs from the one stored on the link, so an unchanged
// origin costs zero Graph writes per run.
public static class ReplicaContentHash
{
    public static string For(
        DateTimeOffset start, DateTimeOffset end, string showAs, bool isAllDay)
    {
        var seed = string.Create(CultureInfo.InvariantCulture,
            $"{start.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}|{end.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}|{showAs.ToLowerInvariant()}|{isAllDay}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
    }
}
