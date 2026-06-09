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
// the body. PublicKeyBase64/NeedsTextKey are the key-admission advertisement and are MERGE
// fields: when omitted (null) the stored values are kept, so a plain preferences save from an
// older caller never wipes a device's published key or its pending-key flag.
public sealed record UpdateClipboardSettingsRequest(
    bool AutoSync, bool Send, bool Receive, string ViewerHotkey, string Density, bool ShowHints,
    string? PublicKeyBase64 = null, bool? NeedsTextKey = null);

public sealed class UpdateClipboardSettingsRequestValidator : AbstractValidator<UpdateClipboardSettingsRequest>
{
    public UpdateClipboardSettingsRequestValidator()
    {
        RuleFor(x => x.Density).Must(d => d is "rich" or "mini");
        RuleFor(x => x.ViewerHotkey).NotEmpty().MaximumLength(40);
        // SPKI public key as base64; matches the column cap and rejects junk early.
        RuleFor(x => x.PublicKeyBase64)
            .MaximumLength(4096)
            .Must(BeValidBase64).WithMessage("Public key must be valid base64.")
            .When(x => x.PublicKeyBase64 is not null);
    }

    private static bool BeValidBase64(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var buffer = new byte[(value.Length / 4 + 1) * 3];
        return Convert.TryFromBase64String(value, buffer, out _);
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
