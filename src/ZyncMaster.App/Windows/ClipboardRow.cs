using System;

namespace ZyncMaster.App.Windows;

public sealed class ClipboardRow
{
    public required string Id { get; init; }    // engine entry id
    public required string Kind { get; init; }  // "text" | "image" | "file"
    public required string Title { get; init; } // text preview, file name, or "Image"
    public required string Meta { get; init; }  // e.g. "DEVLAB2 · 1 min"
}
