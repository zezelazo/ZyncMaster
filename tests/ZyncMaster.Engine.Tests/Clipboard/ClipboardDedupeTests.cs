using System;
using FluentAssertions;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests.Clipboard;

public sealed class ClipboardDedupeTests
{
    // Manual monotonic clock: GetTimestamp ticks only when the test advances it, so the TTL
    // windows can be crossed deterministically without sleeping.
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan by) => _timestamp += by.Ticks;
    }

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
    public void IsEcho_SuppressesEveryMatchWithinTheWindow()
    {
        // The core of the cross-machine echo loop fix: Windows fires WM_CLIPBOARDUPDATE 2-3 times
        // for ONE programmatic set (and RDP redirection re-fires it). A consume-on-first-match
        // scheme let the second fire through as a "new copy". Every match within the window must
        // now be recognized as the same echo, unlimited count.
        var time = new ManualTimeProvider();
        var dedupe = new ClipboardDedupe(timeProvider: time);
        var hash = dedupe.Hash(Text("applied content"));

        dedupe.MarkApplied(hash);

        dedupe.IsEcho(hash).Should().BeTrue();   // first WM_CLIPBOARDUPDATE fire
        time.Advance(TimeSpan.FromMilliseconds(200));
        dedupe.IsEcho(hash).Should().BeTrue();   // second fire
        time.Advance(TimeSpan.FromSeconds(2));
        dedupe.IsEcho(hash).Should().BeTrue();   // RDP re-announce, still inside the window
    }

    [Fact]
    public void IsEcho_ExpiresAfterTheTtl()
    {
        var time = new ManualTimeProvider();
        var dedupe = new ClipboardDedupe(timeProvider: time);
        var hash = dedupe.Hash(Text("applied content"));

        dedupe.MarkApplied(hash);
        dedupe.IsEcho(hash).Should().BeTrue();

        time.Advance(ClipboardDedupe.AppliedEchoTtl + TimeSpan.FromSeconds(1));

        // A genuine user re-copy of the same content after the window must be published again.
        dedupe.IsEcho(hash).Should().BeFalse();
    }

    [Fact]
    public void MarkApplied_Again_RefreshesTheWindow()
    {
        var time = new ManualTimeProvider();
        var dedupe = new ClipboardDedupe(timeProvider: time);
        var hash = dedupe.Hash(Text("applied twice"));

        dedupe.MarkApplied(hash);
        time.Advance(TimeSpan.FromSeconds(10));
        dedupe.MarkApplied(hash); // applied again — window restarts here
        time.Advance(TimeSpan.FromSeconds(10));

        // 20s after the FIRST mark, but only 10s after the refresh: still an echo.
        dedupe.IsEcho(hash).Should().BeTrue();
    }

    [Fact]
    public void IsRecentlyPublished_TrueWithinTheWindow()
    {
        var time = new ManualTimeProvider();
        var dedupe = new ClipboardDedupe(timeProvider: time);
        var hash = dedupe.Hash(Text("just sent"));

        dedupe.MarkPublished(hash);

        dedupe.IsRecentlyPublished(hash).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(5));
        dedupe.IsRecentlyPublished(hash).Should().BeTrue(); // repeated Ctrl+C on the same content
    }

    [Fact]
    public void IsRecentlyPublished_FalseAfterTheTtl()
    {
        var time = new ManualTimeProvider();
        var dedupe = new ClipboardDedupe(timeProvider: time);
        var hash = dedupe.Hash(Text("just sent"));

        dedupe.MarkPublished(hash);
        time.Advance(ClipboardDedupe.RecentPublishTtl + TimeSpan.FromSeconds(1));

        dedupe.IsRecentlyPublished(hash).Should().BeFalse();
    }

    [Fact]
    public void IsRecentlyPublished_FalseForUnknownHash()
    {
        var dedupe = new ClipboardDedupe();
        dedupe.MarkPublished(dedupe.Hash(Text("sent")));

        dedupe.IsRecentlyPublished(dedupe.Hash(Text("other"))).Should().BeFalse();
    }

    [Fact]
    public void AppliedSet_IsBounded()
    {
        var dedupe = new ClipboardDedupe(capacity: 2);
        var h1 = dedupe.Hash(Text("one"));
        var h2 = dedupe.Hash(Text("two"));
        var h3 = dedupe.Hash(Text("three"));

        dedupe.MarkApplied(h1);
        dedupe.MarkApplied(h2);
        dedupe.MarkApplied(h3); // evicts the oldest (h1)

        dedupe.IsEcho(h1).Should().BeFalse();
        dedupe.IsEcho(h2).Should().BeTrue();
        dedupe.IsEcho(h3).Should().BeTrue();
    }

    [Fact]
    public void PublishedWindow_IsSingleSlot_NewContentEndsThePreviousWindow()
    {
        // The A-B-A regression: with a multi-entry publish map, copying A, then B, then A again
        // within the TTL left IsRecentlyPublished(A) true and the genuine re-copy of A was dropped
        // — the peer kept B while the local clipboard held A. Only the LAST published hash may be
        // treated as a recent duplicate (mirroring the server's head-only dedupe).
        var dedupe = new ClipboardDedupe();
        var a = dedupe.Hash(Text("A"));
        var b = dedupe.Hash(Text("B"));

        dedupe.MarkPublished(a);
        dedupe.IsRecentlyPublished(a).Should().BeTrue();

        dedupe.MarkPublished(b);

        dedupe.IsRecentlyPublished(a).Should().BeFalse(); // re-copying A is a fresh user action
        dedupe.IsRecentlyPublished(b).Should().BeTrue();  // B's own multi-fire is still collapsed
    }

    [Fact]
    public void PublishedWindow_StillCollapsesSameHashRepeats()
    {
        var time = new ManualTimeProvider();
        var dedupe = new ClipboardDedupe(timeProvider: time);
        var hash = dedupe.Hash(Text("mashed"));

        dedupe.MarkPublished(hash);

        dedupe.IsRecentlyPublished(hash).Should().BeTrue(); // second WM fire
        time.Advance(TimeSpan.FromSeconds(10));
        dedupe.IsRecentlyPublished(hash).Should().BeTrue(); // Ctrl+C mash, same content
    }

    [Fact]
    public void OnNewContentCaptured_ClosesEchoWindowsForOtherContent()
    {
        // Echo variant of the A-B-A regression: peer-applied A opened a 15s echo window; the user
        // then copies B. The WM fires for A's apply are contiguous, so once the clipboard holds B
        // a later capture of A is a deliberate re-copy — its echo window must be gone.
        var dedupe = new ClipboardDedupe();
        var a = dedupe.Hash(Text("applied A"));
        var c = dedupe.Hash(Text("applied C"));
        var b = dedupe.Hash(Text("captured B"));

        dedupe.MarkApplied(a);
        dedupe.MarkApplied(c);

        dedupe.OnNewContentCaptured(b);

        dedupe.IsEcho(a).Should().BeFalse();
        dedupe.IsEcho(c).Should().BeFalse();
    }

    [Fact]
    public void OnNewContentCaptured_KeepsTheEchoWindowForItsOwnHash()
    {
        var dedupe = new ClipboardDedupe();
        var a = dedupe.Hash(Text("racing apply"));

        dedupe.MarkApplied(a);
        dedupe.OnNewContentCaptured(a);

        dedupe.IsEcho(a).Should().BeTrue();
    }

    [Fact]
    public void AppliedAndPublishedWindows_AreIndependent()
    {
        var dedupe = new ClipboardDedupe();
        var hash = dedupe.Hash(Text("content"));

        dedupe.MarkApplied(hash);

        dedupe.IsEcho(hash).Should().BeTrue();
        dedupe.IsRecentlyPublished(hash).Should().BeFalse();
    }
}
