using System;
using System.Collections.Generic;

namespace ZyncMaster.App.Bridge;

// The shapes the clipboard bridge actions return to the web UI. They are serialized with the
// bridge's camelCase JsonSerializerOptions (UiBridge.JsonOptions), so the property names land on
// the wire exactly as the SHARED BRIDGE CONTRACT specifies.
//
// E2E INVARIANT: a Text history item carries its DECRYPTED plaintext in Text — the UI never sees
// ciphertext. The App decrypts (TextCrypto + the local text key) BEFORE building the item. An Image
// item carries Text=null and, best-effort, a small PNG data URI in ImagePreviewDataUri (null when a
// cheap preview is not available — the UI then shows a typed image tile with the size).

// One row of clipboard history (newest-first in the array). Matches getClipboardHistory /
// the "clipboard:item" push item shape.
public sealed record ClipboardHistoryItem
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "Text"; // "Text" | "Image"
    public string? Text { get; init; }           // decrypted plaintext for Text, else null
    public string? ImagePreviewDataUri { get; init; } // data:image/png;base64,... best-effort, else null
    public long? SizeBytes { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public string OriginDeviceId { get; init; } = "";
    public string? OriginDeviceName { get; init; }
}

// getClipboardDevices result: the user's devices (online + isThis + per-device clipboard settings)
// plus this device's id so the UI can highlight it.
public sealed record ClipboardDevicesView
{
    public string ThisDeviceId { get; init; } = "";
    public IReadOnlyList<ClipboardDeviceView> Devices { get; init; } = new List<ClipboardDeviceView>();

    // App-local opacity (0..100) of the floating hotkey paste panel. Surfaced here so the clipboard
    // settings screen can initialise its slider to the persisted value. NOT a per-device/server
    // setting — it is read from this device's settings.json. Defaults to 70.
    public int PastePanelOpacity { get; init; } = 70;
}

public sealed record ClipboardDeviceView
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Online { get; init; }
    public bool IsThis { get; init; }
    public ClipboardSettingsView Settings { get; init; } = new();
}

// The per-device clipboard settings as the UI edits them. Mirrors the Engine ClipboardSettings but
// lives in the App layer so the wire contract is owned here, not in the Engine.
public sealed record ClipboardSettingsView
{
    public bool AutoSync { get; init; } = true;
    public bool Send { get; init; } = true;
    public bool Receive { get; init; } = true;
    public string ViewerHotkey { get; init; } = "Ctrl+Win+Q";
    public string Density { get; init; } = "rich";
    public bool ShowHints { get; init; } = true;
}
