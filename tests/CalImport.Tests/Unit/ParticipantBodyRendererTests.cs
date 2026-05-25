using System;
using System.Collections.Generic;
using FluentAssertions;
using SyncMaster.CalImport;
using SyncMaster.Core;
using Xunit;

namespace SyncMaster.CalImport.Tests;

public sealed class ParticipantBodyRendererTests
{
    private readonly ParticipantBodyRenderer _sut = new ParticipantBodyRenderer();

    private static List<ParticipantRecord> SampleParticipants() => new List<ParticipantRecord>
    {
        new ParticipantRecord { Name = "Bob",  Email = "bob@x.com",  Type = "required", Response = "accepted" },
        new ParticipantRecord { Name = "Room", Email = "room@x.com", Type = "resource", Response = "none"     },
    };

    [Fact]
    public void BuildBodyForCreate_NoParticipants_OnlyDescription()
    {
        var html = _sut.BuildBodyForCreate("Hello world", Array.Empty<ParticipantRecord>());

        html.Should().Contain("Hello world");
        html.Should().NotContain("calimport:participants");
    }

    [Fact]
    public void BuildBodyForCreate_NoDescription_OnlyMarkers()
    {
        var html = _sut.BuildBodyForCreate("", SampleParticipants());

        html.Should().Contain("calimport:participants:start");
        html.Should().Contain("calimport:participants:end");
        html.Should().Contain("Bob");
        html.Should().Contain("bob@x.com");
    }

    [Fact]
    public void BuildBodyForCreate_EscapesDescription()
    {
        var html = _sut.BuildBodyForCreate("<script>alert(1)</script>", Array.Empty<ParticipantRecord>());

        html.Should().NotContain("<script>");
        html.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void BuildBodyForCreate_ParticipantBlockHasBothMarkers()
    {
        var html = _sut.BuildBodyForCreate("Desc", SampleParticipants());

        html.IndexOf("calimport:participants:start", StringComparison.Ordinal)
            .Should().BeLessThan(html.IndexOf("calimport:participants:end", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_NoMarkersInBody_AppendsNewBlock()
    {
        var existing = "<p>User-written body</p>";
        var result   = _sut.MergeIntoExistingBody(existing, SampleParticipants());

        result.Should().Contain("User-written body");
        result.Should().Contain("calimport:participants:start");
        result.Should().Contain("Bob");
    }

    [Fact]
    public void Merge_WithMarkers_ReplacesBlockPreservingSurrounding()
    {
        var existing =
            "<p>Top</p>\n" +
            "<!-- calimport:participants:start -->\n<ul><li>OLD</li></ul>\n<!-- calimport:participants:end -->\n" +
            "<p>Bottom edited by user</p>";

        var result = _sut.MergeIntoExistingBody(existing, SampleParticipants());

        result.Should().Contain("Top");
        result.Should().Contain("Bottom edited by user");
        result.Should().Contain("Bob");
        result.Should().NotContain("OLD");
    }

    [Fact]
    public void Merge_NoParticipants_WithExistingMarkers_RemovesBlock()
    {
        var existing =
            "<p>Top</p>\n" +
            "<!-- calimport:participants:start -->\n<ul><li>OLD</li></ul>\n<!-- calimport:participants:end -->\n" +
            "<p>Bottom</p>";

        var result = _sut.MergeIntoExistingBody(existing, Array.Empty<ParticipantRecord>());

        result.Should().Contain("Top");
        result.Should().Contain("Bottom");
        result.Should().NotContain("OLD");
        result.Should().NotContain("calimport:participants");
        result.Should().NotContain("Participantes");
    }

    [Fact]
    public void Merge_NoParticipants_NoMarkers_BodyUnchanged()
    {
        var existing = "<p>Plain body</p>";
        _sut.MergeIntoExistingBody(existing, Array.Empty<ParticipantRecord>())
            .Should().Be("Plain body" == "" ? "" : existing);
    }

    [Fact]
    public void Merge_EmptyExistingBody_NoParticipants_ReturnsEmpty()
    {
        _sut.MergeIntoExistingBody("", Array.Empty<ParticipantRecord>()).Should().BeEmpty();
    }

    [Fact]
    public void Merge_RoundTrip_Idempotent()
    {
        var participants = SampleParticipants();
        var body1 = _sut.BuildBodyForCreate("Desc", participants);
        var body2 = _sut.MergeIntoExistingBody(body1, participants);

        // The block content should be stable across a re-render with the same participants.
        body2.Should().Contain("Bob");
        body2.Should().Contain("Room");
        var firstCount = System.Text.RegularExpressions.Regex.Matches(body2, "calimport:participants:start").Count;
        firstCount.Should().Be(1);
    }

    [Fact]
    public void BuildBodyForCreate_NullDescription_DoesNotThrow_AndOmitsDescriptionParagraph()
    {
        // description == null hits the IsNullOrEmpty branch (skip the description <p>)
        // and only emits the participants block (which has its own header paragraph).
        var withNull = _sut.BuildBodyForCreate(description: null!, SampleParticipants());
        var withDesc = _sut.BuildBodyForCreate(description: "Some desc",  SampleParticipants());

        withNull.Should().NotContain("Some desc");
        withNull.Should().Contain("calimport:participants:start");
        withNull.Should().Contain("Bob");
        // The null-description variant has exactly one fewer <p> than the
        // variant that supplies a description.
        var pCountNull = System.Text.RegularExpressions.Regex.Matches(withNull, "<p>").Count;
        var pCountDesc = System.Text.RegularExpressions.Regex.Matches(withDesc, "<p>").Count;
        pCountDesc.Should().Be(pCountNull + 1);
    }

    [Fact]
    public void BuildBodyForCreate_NullDescription_NoParticipants_ReturnsEmpty()
    {
        var html = _sut.BuildBodyForCreate(description: null!, Array.Empty<ParticipantRecord>());
        html.Should().BeEmpty();
    }

    [Fact]
    public void BuildBodyForCreate_NullParticipants_Throws()
    {
        Action act = () => _sut.BuildBodyForCreate("desc", participants: null!);
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("participants");
    }

    [Fact]
    public void MergeIntoExistingBody_NullParticipants_Throws()
    {
        Action act = () => _sut.MergeIntoExistingBody("<p>x</p>", participants: null!);
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("participants");
    }

    [Fact]
    public void BuildBodyForCreate_ParticipantWithEmptyEmail_OmitsAngleBrackets()
    {
        var participants = new List<ParticipantRecord>
        {
            new ParticipantRecord { Name = "NoEmail", Email = "", Type = "required", Response = "accepted" },
        };
        var html = _sut.BuildBodyForCreate("", participants);

        html.Should().Contain("NoEmail");
        html.Should().NotContain("&lt;");
        html.Should().NotContain("&gt;");
        html.Should().Contain("required");
        html.Should().Contain("accepted");
    }

    [Fact]
    public void BuildBodyForCreate_ParticipantWithEmptyTypeAndResponse_OmitsEmDashes()
    {
        var participants = new List<ParticipantRecord>
        {
            new ParticipantRecord { Name = "Solo", Email = "solo@x.com", Type = "", Response = "" },
        };
        var html = _sut.BuildBodyForCreate("", participants);

        html.Should().Contain("Solo");
        html.Should().Contain("solo@x.com");
        html.Should().NotContain(" — ");
    }

    [Fact]
    public void BuildBodyForCreate_ParticipantWithAllEmptyOptionalFields_OnlyName()
    {
        var participants = new List<ParticipantRecord>
        {
            new ParticipantRecord { Name = "OnlyName", Email = "", Type = "", Response = "" },
        };
        var html = _sut.BuildBodyForCreate("", participants);

        html.Should().Contain("OnlyName");
        html.Should().NotContain("&lt;");
        html.Should().NotContain(" — ");
    }

    [Fact]
    public void Merge_BodyContainsOnlyTheBlock_ReplacedByNewBlock()
    {
        // Body that is exclusively the participants block: the regex must match
        // and the replacement must yield a body equal to the new block alone.
        var existing =
            "<!-- calimport:participants:start -->\n<ul><li>OLD</li></ul>\n<!-- calimport:participants:end -->";

        var result = _sut.MergeIntoExistingBody(existing, SampleParticipants());

        result.Should().NotContain("OLD");
        result.Should().Contain("Bob");
        result.Should().Contain("calimport:participants:start");
        result.Should().Contain("calimport:participants:end");
        // Exactly one block present after replacement.
        System.Text.RegularExpressions.Regex.Matches(result, "calimport:participants:start").Count
            .Should().Be(1);
    }

    [Fact]
    public void Merge_BodyContainsOnlyTheBlock_NoParticipants_BecomesEmpty()
    {
        // Same scenario, but replacing with an empty block (no participants)
        // must leave the body empty.
        var existing =
            "<!-- calimport:participants:start -->\n<ul><li>OLD</li></ul>\n<!-- calimport:participants:end -->";

        var result = _sut.MergeIntoExistingBody(existing, Array.Empty<ParticipantRecord>());

        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildBodyForCreate_DescriptionWithCrLfAndLf_BothBranchesExecute()
    {
        // The description contains a Windows CRLF and a Unix LF; the implementation
        // runs the CRLF replace first and then the LF replace, so both branches
        // contribute <br> markers. We assert presence of <br> markers without
        // pinning the exact double-replace shape.
        var description = "line1\r\nline2\nline3";
        var html = _sut.BuildBodyForCreate(description, Array.Empty<ParticipantRecord>());

        html.Should().Contain("line1");
        html.Should().Contain("line2");
        html.Should().Contain("line3");
        html.Should().Contain("<br>");
        // No raw CRLF survives in the output.
        html.Should().NotContain("line1\r\nline2");
    }
}
