namespace ZyncMaster.Server;

// Wire mappers for the clipboard REST surface. The HTTP shapes are camelCase, carry the byte
// payloads as base64 strings and the item type as a string (matching the WS frame the broadcaster
// emits). Kept separate from the EF row mappers so the wire contract never leaks the storage schema.
public static class ClipboardDto
{
    // Domain item -> wire object (anonymous, serialized camelCase by the JSON pipeline).
    public static object ToWire(ClipboardItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new
        {
            id = item.Id,
            type = item.Type.ToString(),
            originDeviceId = item.OriginDeviceId,
            originDeviceName = item.OriginDeviceName,
            createdUtc = item.CreatedUtc,
            sizeBytes = item.SizeBytes,
            payloadBase64 = Convert.ToBase64String(item.Payload),
            thumbnailBase64 = item.Thumbnail is { } thumb ? Convert.ToBase64String(thumb) : null,
            preview = item.Preview,
        };
    }

    // Per-device settings -> wire object.
    public static object ToWire(ClipboardDeviceSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new
        {
            deviceId = s.DeviceId,
            autoSync = s.AutoSync,
            send = s.Send,
            receive = s.Receive,
            viewerHotkey = s.ViewerHotkey,
            density = s.Density,
            showHints = s.ShowHints,
        };
    }

    // Publish request -> domain item. Stamps the resolved user id and decodes the base64 payloads.
    // Throws FormatException on malformed base64 (the endpoint maps that to 400). The Type is known
    // valid (the validator rejects anything but Text/Image and blocks File) before this runs.
    public static ClipboardItem ToDomain(PublishItemRequest req, string userId)
    {
        ArgumentNullException.ThrowIfNull(req);
        var type = Enum.Parse<ClipboardItemType>(req.Type);
        var payload = Convert.FromBase64String(req.PayloadBase64);
        return new ClipboardItem
        {
            Id = req.Id,
            UserId = userId,
            Type = type,
            OriginDeviceId = req.OriginDeviceId,
            OriginDeviceName = req.OriginDeviceName,
            CreatedUtc = DateTimeOffset.UtcNow,
            // The server is authoritative on image size: use the ACTUAL decoded payload length,
            // never the client-supplied SizeBytes (which could understate it to slip past the
            // store's HardMax / per-user image-total guards). Text carries no size.
            SizeBytes = type == ClipboardItemType.Image ? payload.Length : null,
            Payload = payload,
            Thumbnail = string.IsNullOrEmpty(req.ThumbnailBase64)
                ? null
                : Convert.FromBase64String(req.ThumbnailBase64),
            Preview = req.Preview,
        };
    }
}
