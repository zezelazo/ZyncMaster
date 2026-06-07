namespace ZyncMaster.Server;

public enum ClipboardItemType { Text, Image, File }

// A clipboard entry in the shared per-user history. For Text, Payload is the E2E
// ciphertext the server CANNOT read (stored as opaque bytes). For Image, Payload is
// the (server-readable) image bytes and Thumbnail a small preview. SizeBytes is set
// for image/file; null for text. CreatedUtc is the age, always present.
public sealed record ClipboardItem
{
    public required string Id { get; init; }            // uuid (client-generated)
    public required string UserId { get; init; }
    public required ClipboardItemType Type { get; init; }
    public required string OriginDeviceId { get; init; }
    public string? OriginDeviceName { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public long? SizeBytes { get; init; }               // image/file only
    public byte[] Payload { get; init; } = Array.Empty<byte>();   // text=ciphertext, image=bytes
    public byte[]? Thumbnail { get; init; }             // image only
    public string? Preview { get; init; }               // OPTIONAL plaintext preview ONLY for non-E2E types; text => null
}

// Per-device clipboard preferences. Editable for any device of the user, even while
// that device is offline; applied when it reconnects.
public sealed record ClipboardDeviceSettings
{
    public required string DeviceId { get; init; }
    public bool AutoSync { get; init; } = true;   // set OS clipboard on receive
    public bool Send { get; init; } = true;
    public bool Receive { get; init; } = true;
    public string ViewerHotkey { get; init; } = "Ctrl+Win+Q";
    public string Density { get; init; } = "rich"; // "rich" | "mini"
    public bool ShowHints { get; init; } = true;
}

// A wrapped (per-target-device encrypted) copy of the E2E text key, relayed between
// the user's devices. The server NEVER persists or logs this; it only forwards it to
// TargetDeviceId. WrappedKey is opaque to the server.
public sealed record WrappedKeyEnvelope
{
    public required string FromDeviceId { get; init; }
    public required string TargetDeviceId { get; init; }
    public required byte[] WrappedKey { get; init; }
}
