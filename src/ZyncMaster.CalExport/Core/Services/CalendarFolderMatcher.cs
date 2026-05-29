using System;
using System.Collections.Generic;
using System.Linq;

namespace ZyncMaster.CalExport;

public sealed class CalendarFolderMatcher
{
    /// <summary>
    /// Matches <paramref name="requestedNames"/> against <paramref name="available"/> by DisplayName
    /// (case-insensitive). Returns null if nothing matched (means "all"). Calls
    /// <paramref name="onNotFound"/> for each name that had no match.
    /// </summary>
    public IReadOnlyList<CalendarFolderInfo>? Match(
        IEnumerable<string>              requestedNames,
        IReadOnlyList<CalendarFolderInfo> available,
        Action<string>?                  onNotFound = null)
    {
        if (requestedNames == null) throw new ArgumentNullException(nameof(requestedNames));
        if (available      == null) throw new ArgumentNullException(nameof(available));

        var names = requestedNames.ToList();
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

        return matched.Count == 0 ? null : matched;
    }
}
