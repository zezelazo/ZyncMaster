using System;
using ZyncMaster.CalExport;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.CalExport.Tests;

// FIX 3b — "CalExport without Outlook" must be a clean, distinguishable outcome, not a stack dump.
// These pin the exit-code contract Program.cs relies on and the dedicated exception that triggers it.
public sealed class ExitCodesTests
{
    [Fact]
    public void Codes_AreDistinct_And_SuccessIsZero()
    {
        ExitCodes.Success.Should().Be(0);
        ExitCodes.GeneralError.Should().Be(1);
        ExitCodes.InvalidArguments.Should().Be(2);
        ExitCodes.OutlookUnavailable.Should().Be(3);

        // The Outlook-unavailable code must be distinguishable from both the generic failure and the
        // bad-arguments code so the App / scheduler can branch on it.
        ExitCodes.OutlookUnavailable.Should().NotBe(ExitCodes.GeneralError);
        ExitCodes.OutlookUnavailable.Should().NotBe(ExitCodes.InvalidArguments);
    }

    [Fact]
    public void OutlookUnavailableException_CarriesMessage()
    {
        var ex = new OutlookUnavailableException("Could not connect to Outlook.");
        ex.Message.Should().Contain("Could not connect to Outlook");
        ex.Should().BeAssignableTo<Exception>();
    }

    [Fact]
    public void OutlookUnavailableException_PreservesInner()
    {
        var inner = new InvalidOperationException("COM 0x80004005");
        var ex = new OutlookUnavailableException("Could not connect to Outlook.", inner);
        ex.InnerException.Should().BeSameAs(inner);
    }
}
