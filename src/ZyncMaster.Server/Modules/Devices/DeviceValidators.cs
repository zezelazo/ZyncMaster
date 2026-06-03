using FluentValidation;

namespace ZyncMaster.Server;

public sealed record PairStartRequest(string Name);
public sealed record PairCompleteRequest(string PairingId);
public sealed record ApproveRequest(string Code);

public sealed class PairStartRequestValidator : AbstractValidator<PairStartRequest>
{
    public PairStartRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(64);
    }
}

public sealed class PairCompleteRequestValidator : AbstractValidator<PairCompleteRequest>
{
    public PairCompleteRequestValidator()
    {
        RuleFor(x => x.PairingId).NotEmpty();
    }
}

public sealed class ApproveRequestValidator : AbstractValidator<ApproveRequest>
{
    public ApproveRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty();
    }
}

// §A-2 — brokered registration body. NOTE: there is intentionally NO userId field; the device's
// owner is taken from the identity bearer token, never from the body. Platform is optional and
// normalized server-side; an unknown value falls back to "windows".
public sealed record DeviceRegisterRequest(
    string Name,
    string? Platform = null,
    bool HasOutlookCom = false,
    string? AppVersion = null);

public sealed class DeviceRegisterRequestValidator : AbstractValidator<DeviceRegisterRequest>
{
    public DeviceRegisterRequestValidator()
    {
        // Name is OPTIONAL on registration: when blank/null the server mints a unique geek name
        // derived from the user's account (see DeviceNameGenerator). When supplied it is capped so a
        // single field cannot blow past storage limits.
        RuleFor(x => x.Name).MaximumLength(256);
        RuleFor(x => x.AppVersion).MaximumLength(64);
    }
}

// Device self-rename body. NOTE: there is intentionally NO deviceId field; the device renames
// ITSELF — the target id is the ApiKey principal's "deviceId" claim, never a value from the body.
// Name is validated against the server-side trimmed length (1..100).
public sealed record DeviceRenameRequest(string Name);

public sealed class DeviceRenameRequestValidator : AbstractValidator<DeviceRenameRequest>
{
    public DeviceRenameRequestValidator()
    {
        RuleFor(x => x.Name)
            .Must(n => !string.IsNullOrWhiteSpace(n))
            .WithMessage("Device name is required.")
            .Must(n => (n ?? string.Empty).Trim().Length <= 100)
            .WithMessage("Device name must be at most 100 characters.");
    }
}
