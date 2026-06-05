using System;
using System.Collections.Generic;
using System.Linq;

namespace ZyncMaster.CalExport;

public sealed class CalendarFolderMatcher
{
    /// <summary>
    /// Matches <paramref name="requestedNames"/> against <paramref name="available"/> by DisplayName
    /// (case-insensitive). Distinguishes two cases:
    /// <list type="bullet">
    /// <item>NO selection provided (<paramref name="requestedNames"/> empty/null) — returns
    /// <c>null</c>, the codebase contract for "all calendars". This is intentional.</item>
    /// <item>A selection WAS provided but NONE of the requested names matched (e.g. a calendar was
    /// renamed in Outlook) — returns an EMPTY list, NOT null. Falling back to "all" here would
    /// silently export every calendar instead of the user's intended subset.</item>
    /// </list>
    /// Calls <paramref name="onNotFound"/> for each requested name that had no match so the caller
    /// can warn the user about the specific calendars it could not find.
    /// </summary>
    public IReadOnlyList<CalendarFolderInfo>? Match(
        IEnumerable<string>              requestedNames,
        IReadOnlyList<CalendarFolderInfo> available,
        Action<string>?                  onNotFound = null)
    {
        if (requestedNames == null) throw new ArgumentNullException(nameof(requestedNames));
        if (available      == null) throw new ArgumentNullException(nameof(available));

        var names = requestedNames.ToList();
        // No selection at all => "all calendars" (null). Only this case falls back to all.
        if (names.Count == 0)
            return null;

        var matched = new List<CalendarFolderInfo>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            var folder = available.FirstOrDefault(f =>
                string.Equals(f.DisplayName, name, StringComparison.OrdinalIgnoreCase));

            if (folder == null)
            {
                onNotFound?.Invoke(name);
            }
            else if (seenIds.Add(folder.EntryId))
            {
                matched.Add(folder);
            }
        }

        // A selection WAS provided but produced zero matches: return the EMPTY list, never null.
        // Returning null would mean "all" and export every calendar — the opposite of the user's
        // intent. The caller has already been told (via onNotFound) which names were not found.
        return matched;
    }
}
