using FluentValidation;

namespace ZyncMaster.Server;

// Body for publishing a clipboard item to the user's shared history. PayloadBase64 is the
// item bytes (E2E ciphertext for Text; readable image bytes for Image). File is rejected in
// F1a — the validator blocks it explicitly.
public sealed record PublishItemRequest(
    string Id, string Type, string OriginDeviceId, string? OriginDeviceName,
    long? SizeBytes, string PayloadBase64, string? ThumbnailBase64, string? Preview);

public sealed class PublishItemRequestValidator : AbstractValidator<PublishItemRequest>
{
    public PublishItemRequestValidator()
    {
        RuleFor(x => x.Id).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Type).Must(t => t is "Text" or "Image" or "File");
        RuleFor(x => x.OriginDeviceId).NotEmpty().MaximumLength(64);
        RuleFor(x => x.PayloadBase64).NotEmpty();
        RuleFor(x => x.Type).Must(t => t != "File").WithMessage("Files are not supported in F1a.");
    }
}

// Body for updating a device's clipboard settings (PUT). DeviceId comes from the route, not
// the body.
public sealed record UpdateClipboardSettingsRequest(
    bool AutoSync, bool Send, bool Receive, string ViewerHotkey, string Density, bool ShowHints);

public sealed class UpdateClipboardSettingsRequestValidator : AbstractValidator<UpdateClipboardSettingsRequest>
{
    public UpdateClipboardSettingsRequestValidator()
    {
        RuleFor(x => x.Density).Must(d => d is "rich" or "mini");
        RuleFor(x => x.ViewerHotkey).NotEmpty().MaximumLength(40);
    }
}

// Body for relaying a wrapped E2E text key from one of the user's devices to another. The
// server forwards WrappedKeyBase64 to TargetDeviceId without persisting or logging it.
public sealed record RelayKeyRequest(string FromDeviceId, string TargetDeviceId, string WrappedKeyBase64);

public sealed class RelayKeyRequestValidator : AbstractValidator<RelayKeyRequest>
{
    public RelayKeyRequestValidator()
    {
        RuleFor(x => x.FromDeviceId).NotEmpty();
        RuleFor(x => x.TargetDeviceId).NotEmpty();
        RuleFor(x => x.WrappedKeyBase64).NotEmpty();
    }
}
