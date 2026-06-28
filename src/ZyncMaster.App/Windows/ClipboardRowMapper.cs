using System;
using ZyncMaster.App.Bridge;

namespace ZyncMaster.App.Windows;

// Pure mapping from a clipboard history wire item (ClipboardHistoryItem) to the native viewer's
// ClipboardRow. Extracted from App.axaml.cs so the preview/age formatting is unit-testable: the
// reference time is INJECTED (nowUtc) rather than read from the clock, so the relative-age strings
// are deterministic in tests. No Avalonia / no I/O — just string shaping.
internal static class ClipboardRowMapper
{
    // Longest text preview shown on a row before it is ellipsised.
    internal const int TitleMaxChars = 80;

    // Builds the row for one history item. `nowUtc` is the reference time for the relative age, so all
    // rows in one refresh share a single "now" and tests can pin it.
    internal static ClipboardRow ToRow(ClipboardHistoryItem item, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new ClipboardRow
        {
            Id = item.Id,
            Kind = Kind(item.Type),
            Title = Title(item),
            Meta = Meta(item, nowUtc),
        };
    }

    // Maps the wire Type ("Text" | "Image" | "File") to the row kind the native template branches on.
    // Anything unrecognized (or null) falls back to "text".
    internal static string Kind(string? type)
        => string.Equals(type, "Image", StringComparison.OrdinalIgnoreCase) ? "image"
         : string.Equals(type, "File", StringComparison.OrdinalIgnoreCase) ? "file"
         : "text";

    // The primary line of a row: a one-line text preview (capped at TitleMaxChars), or a typed label
    // for image/file. The wire item carries no file name, so a File row shows a generic "File" label.
    internal static string Title(ClipboardHistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.Equals(item.Type, "Image", StringComparison.OrdinalIgnoreCase)) return "Image";
        if (string.Equals(item.Type, "File", StringComparison.OrdinalIgnoreCase)) return "File";
        var text = (item.Text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length == 0) return "(empty)";
        return text.Length > TitleMaxChars ? text[..TitleMaxChars] + "…" : text;
    }

    // The secondary line: "{origin device} · {short age}", e.g. "DEVLAB2 · 1 min". A missing/blank
    // origin device name degrades to "Unknown".
    internal static string Meta(ClipboardHistoryItem item, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(item);
        var device = string.IsNullOrWhiteSpace(item.OriginDeviceName) ? "Unknown" : item.OriginDeviceName!.Trim();
        return $"{device} · {ShortAge(item.CreatedUtc, nowUtc)}";
    }

    // A compact relative age for a history row: "now", "3 min", "2 h", "5 d". A future timestamp
    // (clock skew between devices) clamps to "now" rather than showing a negative age.
    internal static string ShortAge(DateTimeOffset whenUtc, DateTimeOffset nowUtc)
    {
        var delta = nowUtc - whenUtc;
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;
        if (delta.TotalMinutes < 1) return "now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} min";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} h";
        return $"{(int)delta.TotalDays} d";
    }
}
