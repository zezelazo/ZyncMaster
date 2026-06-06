using FluentValidation;

namespace ZyncMaster.Server;

public sealed record PairStartRequest(string Name);

// FIX 1 — Verifier is the PKCE proof minted at /api/pair/start. It is OPTIONAL on the wire (so a
// malformed/legacy body still parses to a clean validation result rather than a 400 storm), but the
// SERVICE enforces it: a pending row created after this change always carries a verifier hash and
// will not release the api key without a matching verifier. The request type carries it so the
// endpoint can forward it to the service.
public sealed record PairCompleteRequest(string PairingId, string? Verifier = null);
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
        // single field cannot blow past storage limits. Capped at 100 to match the rename
        // validator and DeviceNameGenerator.MaxNameLength, so a registered name is always
        // editable through rename without tripping a stricter limit.
        RuleFor(x => x.Name).MaximumLength(100);
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
