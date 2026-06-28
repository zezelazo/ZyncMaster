using System;
using FluentAssertions;
using ZyncMaster.App.Bridge;
using ZyncMaster.App.Windows;
using Xunit;

namespace ZyncMaster.App.Tests;

// ClipboardRowMapper — the pure ClipboardHistoryItem -> ClipboardRow mapping that feeds the native
// clipboard popup. Extracted from App.axaml.cs so the preview/age formatting is deterministic: the
// reference "now" is injected, so the relative-age strings are pinned (no wall-clock flakiness).
public class ClipboardRowMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);

    private static ClipboardHistoryItem Item(
        string type = "Text", string? text = null, string? device = "DEV", DateTimeOffset? created = null)
        => new()
        {
            Id = "id-1",
            Type = type,
            Text = text,
            OriginDeviceName = device,
            CreatedUtc = created ?? Now,
        };

    // ---------------- Kind ----------------

    [Theory]
    [InlineData("Text", "text")]
    [InlineData("text", "text")]
    [InlineData("Image", "image")]
    [InlineData("image", "image")]
    [InlineData("IMAGE", "image")]
    [InlineData("File", "file")]
    [InlineData("FILE", "file")]
    [InlineData("", "text")]
    [InlineData("whatever", "text")]
    [InlineData(null, "text")]
    public void Kind_maps_wire_type_case_insensitively(string? type, string expected)
        => ClipboardRowMapper.Kind(type).Should().Be(expected);

    // ---------------- Title ----------------

    [Fact]
    public void Title_for_image_is_the_typed_label()
        => ClipboardRowMapper.Title(Item(type: "Image", text: "ignored")).Should().Be("Image");

    [Fact]
    public void Title_for_file_is_the_typed_label()
        => ClipboardRowMapper.Title(Item(type: "File", text: "ignored")).Should().Be("File");

    [Fact]
    public void Title_for_text_returns_the_trimmed_content()
        => ClipboardRowMapper.Title(Item(text: "  hello world  ")).Should().Be("hello world");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void Title_for_blank_text_is_the_empty_placeholder(string? text)
        => ClipboardRowMapper.Title(Item(type: "Text", text: text)).Should().Be("(empty)");

    [Fact]
    public void Title_collapses_carriage_returns_and_newlines_to_spaces()
        => ClipboardRowMapper.Title(Item(text: "line1\r\nline2\nline3")).Should().Be("line1  line2 line3");

    [Fact]
    public void Title_keeps_text_at_the_cap_without_an_ellipsis()
    {
        var text = new string('a', ClipboardRowMapper.TitleMaxChars);
        ClipboardRowMapper.Title(Item(text: text)).Should().Be(text);
    }

    [Fact]
    public void Title_truncates_over_the_cap_and_appends_an_ellipsis()
    {
        var text = new string('a', ClipboardRowMapper.TitleMaxChars + 1);
        ClipboardRowMapper.Title(Item(text: text))
            .Should().Be(new string('a', ClipboardRowMapper.TitleMaxChars) + "…");
    }

    // ---------------- ShortAge ----------------

    [Theory]
    [InlineData(0, "now")]
    [InlineData(30, "now")]
    [InlineData(59, "now")]
    [InlineData(60, "1 min")]
    [InlineData(300, "5 min")]
    [InlineData(3540, "59 min")]   // 59 minutes
    [InlineData(3600, "1 h")]
    [InlineData(5400, "1 h")]      // 90 minutes
    [InlineData(82800, "23 h")]    // 23 hours
    [InlineData(86400, "1 d")]
    [InlineData(180000, "2 d")]    // 50 hours
    [InlineData(432000, "5 d")]    // 5 days
    public void ShortAge_formats_elapsed_time(int secondsAgo, string expected)
        => ClipboardRowMapper.ShortAge(Now - TimeSpan.FromSeconds(secondsAgo), Now).Should().Be(expected);

    [Fact]
    public void ShortAge_clamps_a_future_timestamp_to_now()
        => ClipboardRowMapper.ShortAge(Now + TimeSpan.FromMinutes(10), Now).Should().Be("now");

    // ---------------- Meta ----------------

    [Fact]
    public void Meta_combines_origin_device_and_age()
    {
        var item = Item(device: "DEVLAB2", created: Now - TimeSpan.FromMinutes(1));
        ClipboardRowMapper.Meta(item, Now).Should().Be("DEVLAB2 · 1 min");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Meta_uses_Unknown_for_a_blank_device(string? device)
        => ClipboardRowMapper.Meta(Item(device: device, created: Now), Now).Should().Be("Unknown · now");

    [Fact]
    public void Meta_trims_the_device_name()
        => ClipboardRowMapper.Meta(Item(device: "  Lab  ", created: Now), Now).Should().Be("Lab · now");

    // ---------------- ToRow (composition) ----------------

    [Fact]
    public void ToRow_maps_a_text_item()
    {
        var item = new ClipboardHistoryItem
        {
            Id = "abc", Type = "Text", Text = "hello", OriginDeviceName = "Lab",
            CreatedUtc = Now - TimeSpan.FromHours(2),
        };

        var row = ClipboardRowMapper.ToRow(item, Now);

        row.Id.Should().Be("abc");
        row.Kind.Should().Be("text");
        row.Title.Should().Be("hello");
        row.Meta.Should().Be("Lab · 2 h");
    }

    [Fact]
    public void ToRow_maps_an_image_item()
    {
        var row = ClipboardRowMapper.ToRow(Item(type: "Image", device: "Lab", created: Now), Now);
        row.Kind.Should().Be("image");
        row.Title.Should().Be("Image");
        row.Meta.Should().Be("Lab · now");
    }

    [Fact]
    public void ToRow_maps_a_file_item()
    {
        var row = ClipboardRowMapper.ToRow(Item(type: "File", device: "Lab", created: Now), Now);
        row.Kind.Should().Be("file");
        row.Title.Should().Be("File");
    }

    // ---------------- Null guards ----------------

    [Fact]
    public void ToRow_rejects_a_null_item()
    {
        var act = () => ClipboardRowMapper.ToRow(null!, Now);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Title_rejects_a_null_item()
    {
        var act = () => ClipboardRowMapper.Title(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Meta_rejects_a_null_item()
    {
        var act = () => ClipboardRowMapper.Meta(null!, Now);
        act.Should().Throw<ArgumentNullException>();
    }
}
