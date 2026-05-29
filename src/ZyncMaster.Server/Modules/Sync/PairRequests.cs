using FluentValidation;

namespace ZyncMaster.Server;

public sealed record CreatePairRequest
{
    public string? Name { get; init; }
    public Endpoint? Source { get; init; }
    public Endpoint? Destination { get; init; }
    public int IntervalMin { get; init; }
}

public sealed record UpdatePairRequest
{
    public string? Name { get; init; }
    public int? IntervalMin { get; init; }
    public string? State { get; init; }
}

public sealed class CreatePairRequestValidator : AbstractValidator<CreatePairRequest>
{
    private static readonly string[] ValidProviders = { "OutlookCom", "MicrosoftGraph" };

    public CreatePairRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty();
        RuleFor(r => r.IntervalMin).GreaterThanOrEqualTo(1);

        RuleFor(r => r.Source).NotNull();
        RuleFor(r => r.Destination).NotNull();

        When(r => r.Source is not null, () =>
        {
            RuleFor(r => r.Source!.Provider).Must(p => ValidProviders.Contains(p))
                .WithName("source.provider")
                .WithMessage("Provider must be OutlookCom or MicrosoftGraph.");
            RuleFor(r => r.Source!.CalendarId).NotEmpty().WithName("source.calendarId");
        });

        When(r => r.Destination is not null, () =>
        {
            RuleFor(r => r.Destination!.Provider).Must(p => ValidProviders.Contains(p))
                .WithName("destination.provider")
                .WithMessage("Provider must be OutlookCom or MicrosoftGraph.");
            RuleFor(r => r.Destination!.CalendarId).NotEmpty().WithName("destination.calendarId");
        });
    }
}

public sealed class UpdatePairRequestValidator : AbstractValidator<UpdatePairRequest>
{
    private static readonly string[] ValidStates = { "active", "paused", "disabled" };

    public UpdatePairRequestValidator()
    {
        When(r => r.Name is not null, () =>
            RuleFor(r => r.Name).NotEmpty());

        When(r => r.IntervalMin is not null, () =>
            RuleFor(r => r.IntervalMin!.Value).GreaterThanOrEqualTo(1));

        When(r => r.State is not null, () =>
            RuleFor(r => r.State).Must(s => ValidStates.Contains(s))
                .WithMessage("State must be active, paused or disabled."));
    }
}
