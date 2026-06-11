using System;
using System.Collections.Generic;

namespace ZyncMaster.Graph;

// Builds the whitelist ReplicaDraft from a source snapshot + the user's manual mask title.
// There is deliberately NO overload that takes a default title: an empty mask throws, so no
// code path can ever fall back to the source subject (privacy invariant 1).
public sealed class ReplicaDraftBuilder
{
    private static readonly HashSet<string> ValidShowAs =
        new(StringComparer.OrdinalIgnoreCase) { "free", "tentative", "busy", "oof", "workingElsewhere" };

    public ReplicaDraft Build(SourceEventSnapshot source, string maskTitle)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(maskTitle))
            throw new ArgumentException(
                "maskTitle is required — a replica NEVER inherits the source subject.",
                nameof(maskTitle));

        return new ReplicaDraft
        {
            MaskTitle = maskTitle.Trim(),
            Start = source.Start,
            End = source.End,
            TimeZoneId = string.IsNullOrWhiteSpace(source.TimeZoneId) ? "UTC" : source.TimeZoneId,
            IsAllDay = source.IsAllDay,
            ShowAs = ValidShowAs.Contains(source.ShowAs) ? source.ShowAs : "busy",
            SourceEventId = source.StableId,
        };
    }
}
