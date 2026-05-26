using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using FluentAssertions;
using SyncMaster.Graph;
using SyncMaster.Core;
using Xunit;

namespace SyncMaster.Graph.Tests;

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
    public void BuildBodyForCreate_NoDescription_OnlyBlock()
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
    public void BuildBodyForCreate_ParticipantsRenderedBeforeDescription()
    {
        var html = _sut.BuildBodyForCreate("My description here", SampleParticipants());

        html.IndexOf("calimport:participants:start", StringComparison.Ordinal)
            .Should().BeLessThan(html.IndexOf("My description here", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildBodyForCreate_RendersHtmlTableWithHeaders()
    {
        var html = _sut.BuildBodyForCreate("", SampleParticipants());

        html.Should().Contain("<table");
        html.Should().Contain("<th>Name</th>");
        html.Should().Contain("<th>Email</th>");
        html.Should().Contain("<th>Type</th>");
        html.Should().Contain("<th>Response</th>");
        html.Should().Contain("<td>Bob</td>");
        html.Should().Contain("<td>bob@x.com</td>");
        html.Should().Contain("<td>required</td>");
        html.Should().Contain("<td>accepted</td>");
    }

    [Fact]
    public void BuildBodyForCreate_HeaderIsEnglish()
    {
        var html = _sut.BuildBodyForCreate("", SampleParticipants());

        html.Should().Contain("Participants");
        html.Should().NotContain("Participantes");
    }

    [Fact]
    public void BuildBodyForCreate_EscapesParticipantFields()
    {
        var participants = new List<ParticipantRecord>
        {
            new ParticipantRecord { Name = "A<b>", Email = "x@y.com", Type = "required", Response = "none" },
        };
        var html = _sut.BuildBodyForCreate("", participants);

        html.Should().Contain("A&lt;b&gt;");
        html.Should().NotContain("<td>A<b></td>");
    }

    [Fact]
    public void Merge_NoMarkersInBody_PrependsBlockBeforeExistingBody()
    {
        var existing = "<p>User-written body</p>";
        var result   = _sut.MergeIntoExistingBody(existing, SampleParticipants());

        result.Should().Contain("User-written body");
        result.Should().Contain("calimport:participants:start");
        result.Should().Contain("Bob");
        // Block prepended: it appears before the user's existing body.
        result.IndexOf("calimport:participants:start", StringComparison.Ordinal)
              .Should().BeLessThan(result.IndexOf("User-written body", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_WithMarkers_ReplacesBlockPreservingSurrounding()
    {
        var existing =
            "<p>Top</p>\n" +
            "<!-- calimport:participants:start -->\n<table><tr><td>OLD</td></tr></table>\n<!-- calimport:participants:end -->\n" +
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
            "<!-- calimport:participants:start -->\n<table><tr><td>OLD</td></tr></table>\n<!-- calimport:participants:end -->\n" +
            "<p>Bottom</p>";

        var result = _sut.MergeIntoExistingBody(existing, Array.Empty<ParticipantRecord>());

        result.Should().Contain("Top");
        result.Should().Contain("Bottom");
        result.Should().NotContain("OLD");
        result.Should().NotContain("calimport:participants");
        result.Should().NotContain("Participants");
    }

    [Fact]
    public void Merge_NoParticipants_NoMarkers_BodyUnchanged()
    {
        var existing = "<p>Plain body</p>";
        _sut.MergeIntoExistingBody(existing, Array.Empty<ParticipantRecord>()).Should().Be(existing);
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

        body2.Should().Contain("Bob");
        body2.Should().Contain("Room");
        Regex.Matches(body2, "calimport:participants:start").Count.Should().Be(1);
    }

    [Fact]
    public void BuildBodyForCreate_NullDescription_NoParticipants_ReturnsEmpty()
    {
        var html = _sut.BuildBodyForCreate(description: null!, Array.Empty<ParticipantRecord>());
        html.Should().BeEmpty();
    }

    [Fact]
    public void BuildBodyForCreate_NullDescription_WithParticipants_OmitsDescription()
    {
        var html = _sut.BuildBodyForCreate(description: null!, SampleParticipants());

        html.Should().Contain("calimport:participants:start");
        html.Should().Contain("Bob");
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
    public void BuildBodyForCreate_ParticipantWithEmptyFields_RendersEmptyCells()
    {
        var participants = new List<ParticipantRecord>
        {
            new ParticipantRecord { Name = "Solo", Email = "", Type = "", Response = "" },
        };
        var html = _sut.BuildBodyForCreate("", participants);

        html.Should().Contain("<td>Solo</td>");
        html.Should().Contain("<td></td>"); // empty email/type/response cells
    }

    [Fact]
    public void Merge_BodyContainsOnlyTheBlock_ReplacedByNewBlock()
    {
        var existing =
            "<!-- calimport:participants:start -->\n<table><tr><td>OLD</td></tr></table>\n<!-- calimport:participants:end -->";

        var result = _sut.MergeIntoExistingBody(existing, SampleParticipants());

        result.Should().NotContain("OLD");
        result.Should().Contain("Bob");
        Regex.Matches(result, "calimport:participants:start").Count.Should().Be(1);
    }

    [Fact]
    public void Merge_BodyContainsOnlyTheBlock_NoParticipants_BecomesEmpty()
    {
        var existing =
            "<!-- calimport:participants:start -->\n<table><tr><td>OLD</td></tr></table>\n<!-- calimport:participants:end -->";

        var result = _sut.MergeIntoExistingBody(existing, Array.Empty<ParticipantRecord>());

        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildBodyForCreate_DescriptionWithCrLfAndLf_ConvertsToBr()
    {
        var description = "line1\r\nline2\nline3";
        var html = _sut.BuildBodyForCreate(description, Array.Empty<ParticipantRecord>());

        html.Should().Contain("line1");
        html.Should().Contain("line2");
        html.Should().Contain("line3");
        html.Should().Contain("<br>");
        html.Should().NotContain("line1\r\nline2");
    }
}
