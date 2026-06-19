namespace ZyncMaster.Engine;

public enum ClipboardEntryType { Text, Image, File }

public sealed record ClipboardEntry
{
    public required string Id { get; init; }
    public required ClipboardEntryType Type { get; init; }
    public string? Text { get; init; }
    // Encrypted text payload used ONLY on the transport boundary: the ClipboardService sets this
    // (= TextCrypto.Encrypt(key, Text)) before PublishAsync, and the transport delivers it here on
    // receive (Text stays null until the service decrypts it). For images the bytes ride ImageBytes.
    public byte[]? CipherText { get; init; }
    public byte[]? ImageBytes { get; init; }
    public byte[]? Thumbnail { get; init; }
    public long? SizeBytes { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public string OriginDeviceId { get; init; } = "";
    public string? OriginDeviceName { get; init; }

    // File items: FileName is the display name (rides the wire as the metadata "preview"). FileBytes is
    // the file content used ONLY locally on the publish side — the service uploads it to the lazy-blob
    // store, never on the item frame — and is null on receive (the bytes are fetched on demand by id).
    // A File whose content is over the size cap carries FileName + SizeBytes but FileBytes == null.
    public string? FileName { get; init; }
    public byte[]? FileBytes { get; init; }
}

public sealed record ClipboardSettings
{
    public bool AutoSync { get; init; } = true;
    public bool Send { get; init; } = true;
    public bool Receive { get; init; } = true;
    public string ViewerHotkey { get; init; } = "Ctrl+Win+Q";
    public string Density { get; init; } = "rich";
    public bool ShowHints { get; init; } = true;

    // Key-admission advertisement, carried over the same per-device settings surface. Both are
    // NULLABLE on purpose: null means "leave the stored value alone" — the server PATCH merges
    // omitted fields, so a plain preferences save never wipes a device's published public key or
    // its pending needs-key flag. The server always echoes concrete values back on GET and on the
    // settings broadcast.
    public string? PublicKeyBase64 { get; init; }
    public bool? NeedsTextKey { get; init; }
}

// One row of the clipboard device roster (GET /api/clipboard/devices) — the key-admission view: a
// key-holder scans it for peers advertising NeedsTextKey with a published public key, wraps the
// text key against that key and relays it. Online comes from the server's live WS registry.
public sealed record ClipboardDeviceKeyInfo
{
    public required string DeviceId { get; init; }
    public string? Name { get; init; }
    public bool Online { get; init; }
    public bool NeedsTextKey { get; init; }
    public string? PublicKeyBase64 { get; init; }
}
