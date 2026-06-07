using FluentAssertions;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests.Clipboard;

public sealed class ClipboardDedupeTests
{
    private static ClipboardEntry Text(string text) => new()
    {
        Id = "x",
        Type = ClipboardEntryType.Text,
        Text = text,
    };

    private static ClipboardEntry Image(byte[] bytes) => new()
    {
        Id = "x",
        Type = ClipboardEntryType.Image,
        ImageBytes = bytes,
    };

    [Fact]
    public void Hash_IsStableForEqualContent()
    {
        var dedupe = new ClipboardDedupe();

        dedupe.Hash(Text("hello")).Should().Be(dedupe.Hash(Text("hello")));
    }

    [Fact]
    public void Hash_DiffersForDifferentText()
    {
        var dedupe = new ClipboardDedupe();

        dedupe.Hash(Text("hello")).Should().NotBe(dedupe.Hash(Text("world")));
    }

    [Fact]
    public void Hash_DiffersForDifferentType_SameBytes()
    {
        var dedupe = new ClipboardDedupe();
        var bytes = System.Text.Encoding.UTF8.GetBytes("hello");

        dedupe.Hash(Text("hello")).Should().NotBe(dedupe.Hash(Image(bytes)));
    }

    [Fact]
    public void Hash_IsStableForEqualImageContent()
    {
        var dedupe = new ClipboardDedupe();
        var a = new byte[] { 1, 2, 3, 4 };
        var b = new byte[] { 1, 2, 3, 4 };

        dedupe.Hash(Image(a)).Should().Be(dedupe.Hash(Image(b)));
    }

    [Fact]
    public void IsEcho_TrueAfterMarkApplied()
    {
        var dedupe = new ClipboardDedupe();
        var hash = dedupe.Hash(Text("copied"));

        dedupe.MarkApplied(hash);

        dedupe.IsEcho(hash).Should().BeTrue();
    }

    [Fact]
    public void IsEcho_FalseForUnknownHash()
    {
        var dedupe = new ClipboardDedupe();
        dedupe.MarkApplied(dedupe.Hash(Text("copied")));

        dedupe.IsEcho(dedupe.Hash(Text("other"))).Should().BeFalse();
    }

    [Fact]
    public void IsEcho_ConsumedOnce()
    {
        var dedupe = new ClipboardDedupe();
        var hash = dedupe.Hash(Text("copied"));
        dedupe.MarkApplied(hash);

        dedupe.IsEcho(hash).Should().BeTrue();
        dedupe.IsEcho(hash).Should().BeFalse();
    }

    [Fact]
    public void RecentSet_IsBounded()
    {
        var dedupe = new ClipboardDedupe(capacity: 2);
        var h1 = dedupe.Hash(Text("one"));
        var h2 = dedupe.Hash(Text("two"));
        var h3 = dedupe.Hash(Text("three"));

        dedupe.MarkApplied(h1);
        dedupe.MarkApplied(h2);
        dedupe.MarkApplied(h3); // evicts the oldest (h1)

        dedupe.IsEcho(h1).Should().BeFalse();
        dedupe.IsEcho(h3).Should().BeTrue();
        dedupe.IsEcho(h2).Should().BeTrue();
    }
}
