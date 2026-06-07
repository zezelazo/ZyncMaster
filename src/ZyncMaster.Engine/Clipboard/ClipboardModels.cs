namespace ZyncMaster.Engine;

public enum ClipboardEntryType { Text, Image }

public sealed record ClipboardEntry
{
    public required string Id { get; init; }
    public required ClipboardEntryType Type { get; init; }
    public string? Text { get; init; }
    public byte[]? ImageBytes { get; init; }
    public byte[]? Thumbnail { get; init; }
    public long? SizeBytes { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public string OriginDeviceId { get; init; } = "";
    public string? OriginDeviceName { get; init; }
}

public sealed record ClipboardSettings
{
    public bool AutoSync { get; init; } = true;
    public bool Send { get; init; } = true;
    public bool Receive { get; init; } = true;
    public string ViewerHotkey { get; init; } = "Ctrl+Win+Q";
    public string Density { get; init; } = "rich";
    public bool ShowHints { get; init; } = true;
}
