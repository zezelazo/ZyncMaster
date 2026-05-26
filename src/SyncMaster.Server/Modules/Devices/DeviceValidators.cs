using FluentValidation;

namespace SyncMaster.Server;

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
