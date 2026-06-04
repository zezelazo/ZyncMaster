using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Server;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// §A-2 — cross-representation self-mirror dedupe BY MAILBOX. The same Microsoft mailbox connected
// both as a legacy UPN account and as a pool account (distinct Guid) does NOT collapse on
// accountId; PairEndpoints.IsSameSourceAndDestinationAsync must catch it by comparing the resolved
// mailbox email + calendar id. Driven directly (internal) against a fake adapter so the resolution
// policy is pinned without the cookie-auth + EF endpoint pipeline.
public sealed class PairMailboxDedupeTests
{
    // Fake bridge: maps a known set of accountRefs to canonical accountIds and accountIds to
    // CalendarAccounts (with AccountEmail). Mirrors the real adapter's two-step resolution.
    private sealed class FakeAdapter : ILegacyConnectedAccountAdapter
    {
        private readonly System.Collections.Generic.Dictionary<string, string> _refToId = new(StringComparer.Ordinal);
        private readonly System.Collections.Generic.Dictionary<string, string> _idToEmail = new(StringComparer.Ordinal);

        public void Map(string accountRef, string accountId, string email)
        {
            _refToId[accountRef] = accountId;
            _idToEmail[accountId] = email;
        }

        public string DeriveAccountId(string userId, string? accountRef) => accountRef ?? "";

        public Task<string> ResolveAccountIdAsync(string? accountRef, CancellationToken ct = default) =>
            Task.FromResult(_refToId.TryGetValue(accountRef ?? "", out var id) ? id : (accountRef ?? ""));

        public Task<CalendarAccount?> ResolveAsync(string accountId, CancellationToken ct = default)
        {
            if (!_idToEmail.TryGetValue(accountId, out var email))
                return Task.FromResult<CalendarAccount?>(null);
            return Task.FromResult<CalendarAccount?>(new CalendarAccount
            {
                Id = accountId,
                UserId = "u",
                Kind = AccountKind.Graph,
                Provider = "microsoft",
                AccountEmail = email,
                Scope = AccountScope.ReadWrite,
                ConnectedAt = DateTimeOffset.UnixEpoch,
            });
        }

        public Task<string?> ResolveRefreshTokenAsync(string accountId, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task UpdateRefreshTokenAsync(string accountId, string refreshToken, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static Endpoint Graph(string accountRef, string calendarId) =>
        new() { Provider = ProviderRegistry.MicrosoftGraph, AccountRef = accountRef, CalendarId = calendarId };

    [Fact]
    public async Task Legacy_and_pool_endpoints_for_the_same_mailbox_and_calendar_collapse()
    {
        var adapter = new FakeAdapter();
        // Legacy ref "user@x" -> derived id; pool ref "pool-guid" -> fresh Guid; SAME mailbox.
        adapter.Map("user@x", "legacy-derived-id", "user@X");
        adapter.Map("pool-guid", "fresh-guid", "USER@x");

        var same = await PairEndpoints.IsSameSourceAndDestinationAsync(
            Graph("user@x", "cal1"), Graph("pool-guid", "cal1"), adapter, CancellationToken.None);

        same.Should().BeTrue("the same mailbox + same calendar is a self-mirror even across representations");
    }

    [Fact]
    public async Task Different_mailboxes_same_calendar_id_are_allowed()
    {
        var adapter = new FakeAdapter();
        adapter.Map("user@x", "legacy-derived-id", "user@x");
        adapter.Map("pool-guid", "fresh-guid", "other@y");

        var same = await PairEndpoints.IsSameSourceAndDestinationAsync(
            Graph("user@x", "cal1"), Graph("pool-guid", "cal1"), adapter, CancellationToken.None);

        same.Should().BeFalse();
    }

    [Fact]
    public async Task Same_mailbox_different_calendars_are_allowed()
    {
        var adapter = new FakeAdapter();
        adapter.Map("user@x", "legacy-derived-id", "user@x");
        adapter.Map("pool-guid", "fresh-guid", "user@x");

        var same = await PairEndpoints.IsSameSourceAndDestinationAsync(
            Graph("user@x", "cal1"), Graph("pool-guid", "cal2"), adapter, CancellationToken.None);

        same.Should().BeFalse();
    }

    [Fact]
    public async Task Same_account_id_still_collapses_on_same_calendar()
    {
        var adapter = new FakeAdapter();
        // Both refs resolve to the SAME accountId (the existing primary path).
        adapter.Map("default", "same-id", "user@x");

        var same = await PairEndpoints.IsSameSourceAndDestinationAsync(
            Graph("default", "cal1"), Graph("default", "cal1"), adapter, CancellationToken.None);

        same.Should().BeTrue();
    }

    [Fact]
    public async Task Unresolved_blank_mailbox_does_not_match()
    {
        var adapter = new FakeAdapter();
        // Distinct ids, neither resolves to a mailbox email -> must NOT be treated as a self-mirror.
        adapter.Map("a", "id-a", "");
        adapter.Map("b", "id-b", "");

        var same = await PairEndpoints.IsSameSourceAndDestinationAsync(
            Graph("a", "cal1"), Graph("b", "cal1"), adapter, CancellationToken.None);

        same.Should().BeFalse();
    }
}
