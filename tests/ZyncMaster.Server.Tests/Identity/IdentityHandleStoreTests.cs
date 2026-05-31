using System;
using FluentAssertions;
using ZyncMaster.Server;
using Xunit;

namespace ZyncMaster.Server.Tests.Identity;

// One-time, 60s loopback handles (plan v2 §A-1). A mutable clock drives expiry deterministically.
public class IdentityHandleStoreTests
{
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void IssueHandle_returns_32_char_handle()
    {
        var store = new InMemoryIdentityHandleStore(new MutableClock(DateTimeOffset.UnixEpoch));

        var handle = store.IssueHandle("the-token");

        handle.Should().HaveLength(32);
    }

    [Fact]
    public void Consume_returns_token_for_live_handle()
    {
        var store = new InMemoryIdentityHandleStore(new MutableClock(DateTimeOffset.UnixEpoch));
        var handle = store.IssueHandle("the-token");

        store.ConsumeHandle(handle).Should().Be("the-token");
    }

    [Fact]
    public void Consume_is_one_time_second_call_returns_null()
    {
        var store = new InMemoryIdentityHandleStore(new MutableClock(DateTimeOffset.UnixEpoch));
        var handle = store.IssueHandle("the-token");

        store.ConsumeHandle(handle).Should().Be("the-token");
        store.ConsumeHandle(handle).Should().BeNull();
    }

    [Fact]
    public void Consume_unknown_handle_returns_null()
    {
        var store = new InMemoryIdentityHandleStore(new MutableClock(DateTimeOffset.UnixEpoch));

        store.ConsumeHandle("does-not-exist").Should().BeNull();
        store.ConsumeHandle("").Should().BeNull();
    }

    [Fact]
    public void Consume_returns_null_after_60s_expiry()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var store = new InMemoryIdentityHandleStore(clock);
        var handle = store.IssueHandle("the-token");

        clock.Advance(TimeSpan.FromSeconds(61));

        store.ConsumeHandle(handle).Should().BeNull();
    }

    [Fact]
    public void Consume_still_returns_token_just_before_expiry()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var store = new InMemoryIdentityHandleStore(clock);
        var handle = store.IssueHandle("the-token");

        clock.Advance(TimeSpan.FromSeconds(59));

        store.ConsumeHandle(handle).Should().Be("the-token");
    }
}
