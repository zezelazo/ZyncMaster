using System.Linq;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// Direct unit coverage of the FluentValidation validators behind /api/pairs. The endpoint
// tests prove a couple of 400s end-to-end; these pin the rule-by-rule behavior (valid shapes,
// each individual failure, and the property names the panel binds error messages to).
public class CreatePairRequestValidatorTests
{
    private static readonly CreatePairRequestValidator Sut = new();

    private static Endpoint Outlook() => new() { Provider = "OutlookCom", CalendarId = "outlook" };
    private static Endpoint Graph() => new() { Provider = "MicrosoftGraph", AccountRef = "a@test", CalendarId = "dst" };

    private static CreatePairRequest Valid() => new()
    {
        Name = "My pair",
        Source = Outlook(),
        Destination = Graph(),
        IntervalMin = 15,
    };

    [Fact]
    public void Valid_request_passes()
    {
        Sut.Validate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Empty_name_fails()
    {
        Sut.Validate(Valid() with { Name = "" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Interval_below_one_fails()
    {
        Sut.Validate(Valid() with { IntervalMin = 0 }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Null_source_and_destination_fail()
    {
        var result = Sut.Validate(Valid() with { Source = null, Destination = null });
        result.IsValid.Should().BeFalse();
        result.Errors.Select(e => e.PropertyName).Should().Contain(new[] { "Source", "Destination" });
    }

    [Theory]
    [InlineData("Imap")]
    [InlineData("")]
    [InlineData("microsoftgraph")] // case-sensitive: lower-case is not a valid provider
    public void Unknown_source_provider_fails_with_provider_message(string provider)
    {
        var result = Sut.Validate(Valid() with { Source = new Endpoint { Provider = provider, CalendarId = "c" } });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Provider must be OutlookCom or MicrosoftGraph.");
    }

    [Fact]
    public void Empty_destination_calendar_id_fails()
    {
        var result = Sut.Validate(Valid() with
        {
            Destination = new Endpoint { Provider = "MicrosoftGraph", AccountRef = "a@test", CalendarId = "" },
        });

        result.IsValid.Should().BeFalse();
        // The rule is named "destination.calendarId" so the panel can bind the message to the field.
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("destination.calendarId"));
    }
}

public class UpdatePairRequestValidatorTests
{
    private static readonly UpdatePairRequestValidator Sut = new();

    [Fact]
    public void Empty_update_passes_all_fields_optional()
    {
        // Every field is nullable and only validated when present; an empty patch is valid.
        Sut.Validate(new UpdatePairRequest()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("active")]
    [InlineData("paused")]
    [InlineData("disabled")]
    public void Valid_states_pass(string state)
    {
        Sut.Validate(new UpdatePairRequest { State = state }).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("running")]
    [InlineData("Active")] // case-sensitive
    [InlineData("")]
    public void Invalid_state_fails(string state)
    {
        Sut.Validate(new UpdatePairRequest { State = state }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Provided_empty_name_fails()
    {
        Sut.Validate(new UpdatePairRequest { Name = "" }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Provided_interval_below_one_fails()
    {
        Sut.Validate(new UpdatePairRequest { IntervalMin = 0 }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Provided_valid_interval_and_name_pass()
    {
        Sut.Validate(new UpdatePairRequest { Name = "Renamed", IntervalMin = 5 }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Valid_source_endpoint_passes()
    {
        Sut.Validate(new UpdatePairRequest
        {
            Source = new Endpoint { Provider = "MicrosoftGraph", CalendarId = "c", CalendarName = "C" },
        }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Source_with_invalid_provider_fails()
    {
        Sut.Validate(new UpdatePairRequest
        {
            Source = new Endpoint { Provider = "Bogus", CalendarId = "c" },
        }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Source_with_empty_calendar_id_fails()
    {
        Sut.Validate(new UpdatePairRequest
        {
            Source = new Endpoint { Provider = "MicrosoftGraph", CalendarId = "" },
        }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Destination_with_invalid_provider_fails()
    {
        Sut.Validate(new UpdatePairRequest
        {
            Destination = new Endpoint { Provider = "Nope", CalendarId = "c" },
        }).IsValid.Should().BeFalse();
    }
}
