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
        RuleFor(x => x.Name).NotEmpty().MaximumLength(256);
        RuleFor(x => x.AppVersion).MaximumLength(64);
    }
}
