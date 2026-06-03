using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ZyncMaster.Server;

// Generates a friendly, geeky default name for a device that has none yet. The scheme combines a
// saga character (Lord of the Rings + The Matrix) with a slug derived from the user's account
// (email local-part or display name): "{character}-{accountSlug}", e.g. "gandalf-zezelazo".
//
// The result is UNIQUE (case-insensitive) within the names already taken by that user's devices,
// and never longer than 100 chars (the rename validator's ceiling). The pick is DETERMINISTIC: the
// character index is derived from a stable hash of the account slug, so the same account always
// starts from the same character and the output is reproducible in tests — no Random.Shared / new
// Random(). When the base name is taken, it walks the shuffled character pool (still hash-seeded)
// and then appends a numeric suffix, guaranteeing it always returns a free name.
//
// PURE / no IO: the caller supplies the account identifier (email or display name) and the set of
// names already in use. This keeps it trivially unit-testable and reusable from registration and
// the /me self-heal path alike.
public sealed class DeviceNameGenerator
{
    public const int MaxNameLength = 100;

    // Clean, kebab-case saga characters. LOTR + The Matrix, no offensive / villainous picks.
    private static readonly string[] Characters =
    {
        // Lord of the Rings
        "frodo", "gandalf", "aragorn", "legolas", "gimli", "samwise", "boromir",
        "galadriel", "elrond", "eowyn", "faramir", "theoden", "bilbo", "merry",
        "pippin", "treebeard", "arwen", "celeborn", "radagast", "eomer",
        // The Matrix
        "neo", "trinity", "morpheus", "oracle", "niobe", "switch", "tank",
        "dozer", "cypher", "apoc", "mouse", "link", "sparks", "ghost",
    };

    private const string FallbackSlug = "device";

    // Composes a unique device name for the given account. accountEmailOrName may be an email
    // ("zeze@msn.com" -> "zezelazo") or a display name ("Zeze Lazo" -> "zeze-lazo"). existingNames
    // is the set of names already taken by the user's devices; the returned name is guaranteed not
    // to collide with any of them (case-insensitive) and to be at most MaxNameLength chars.
    public string Generate(string? accountEmailOrName, IReadOnlyCollection<string> existingNames)
    {
        ArgumentNullException.ThrowIfNull(existingNames);

        var slug = ToSlug(accountEmailOrName);
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

        // Deterministic, account-stable ordering of the character pool: rotate the pool by a hash
        // of the slug so different accounts start from different characters, yet a given account is
        // reproducible. First free "{character}-{slug}" wins.
        var start = StableIndex(slug, Characters.Length);
        for (var i = 0; i < Characters.Length; i++)
        {
            var character = Characters[(start + i) % Characters.Length];
            var candidate = Clamp($"{character}-{slug}");
            if (taken.Add(candidate))
                return candidate;
        }

        // Every "{character}-{slug}" is taken: fall back to the seed character + a numeric suffix,
        // walking n until a free name appears. Guaranteed to terminate.
        var baseCharacter = Characters[start];
        for (var n = 2; ; n++)
        {
            var candidate = Clamp($"{baseCharacter}-{slug}-{n.ToString(CultureInfo.InvariantCulture)}", reserve: SuffixWidth(n));
            if (taken.Add(candidate))
                return candidate;
        }
    }

    // Normalizes an email local-part or a display name into ascii kebab-case: strips accents,
    // lowercases, collapses any run of non-alphanumerics into a single hyphen, and trims hyphens.
    // Email input is reduced to its local-part (before the @) first. Empty / unusable input falls
    // back to a stable placeholder so the result is always a valid, non-empty slug.
    public static string ToSlug(string? accountEmailOrName)
    {
        if (string.IsNullOrWhiteSpace(accountEmailOrName))
            return FallbackSlug;

        var raw = accountEmailOrName.Trim();
        var atIndex = raw.IndexOf('@');
        if (atIndex > 0)
            raw = raw[..atIndex];

        // Decompose to separate base letters from combining diacritics, then drop the marks.
        var normalized = raw.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        var lastWasHyphen = false;
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen && sb.Length > 0)
            {
                sb.Append('-');
                lastWasHyphen = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? FallbackSlug : slug;
    }

    // Stable, non-cryptographic-need-but-deterministic index in [0, modulo). Uses SHA-256 over the
    // UTF-8 slug so it is reproducible across runs and platforms (unlike string.GetHashCode, which
    // is randomized per process). modulo is always >= 1 here (the pool is non-empty).
    private static int StableIndex(string slug, int modulo)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(slug));
        // First 4 bytes as an unsigned int, then mod into range.
        var value = ((uint)hash[0] << 24) | ((uint)hash[1] << 16) | ((uint)hash[2] << 8) | hash[3];
        return (int)(value % (uint)modulo);
    }

    // Ensures the name fits MaxNameLength. When over, the slug portion is trimmed (never the
    // character prefix, which keeps names readable). reserve keeps room for a trailing suffix that
    // was appended AFTER the slug (e.g. "-2") so trimming the slug does not eat the suffix.
    private static string Clamp(string candidate, int reserve = 0)
    {
        if (candidate.Length <= MaxNameLength)
            return candidate;

        // Trim from the slug region while preserving the leading character and any trailing suffix.
        var overflow = candidate.Length - MaxNameLength;
        var firstHyphen = candidate.IndexOf('-');
        if (firstHyphen < 0)
            return candidate[..MaxNameLength];

        // Region we may trim: after "character-" up to the reserved trailing suffix.
        var slugStart = firstHyphen + 1;
        var slugEnd = candidate.Length - reserve;
        var trimmable = slugEnd - slugStart;
        if (trimmable <= 0)
            return candidate[..MaxNameLength];

        var cut = Math.Min(overflow, trimmable);
        var head = candidate[..(slugEnd - cut)].TrimEnd('-');
        var tail = candidate[slugEnd..];
        return head + tail;
    }

    private static int SuffixWidth(int n) => 1 + n.ToString(CultureInfo.InvariantCulture).Length; // "-" + digits
}
