using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using ZyncMaster.Graph;
using ZyncMaster.Server.Modules.Calendar;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// §C-5 — the account-aware token provider resolves the refresh token by accountId through the
// legacy adapter. These tests prove it works for a pool account AND for a legacy account bridged
// by the adapter (so the existing single-account sync keeps getting its token), and that it
// caches / force-refreshes / rotates exactly like the per-UPN provider it replaces.
public sealed class AccountAwareGraphTokenProviderTests
{
    private const string UserId = "user-1";

    private sealed class FakeTokenService : IMicrosoftTokenService
    {
        private readonly Func<string, TokenResult> _refresh;
        public int RefreshCallCount { get; private set; }
        public string? LastRefreshToken { get; private set; }

        public FakeTokenService(Func<string, TokenResult> refresh) => _refresh = refresh;

        public Task<TokenResult> ExchangeCodeAsync(string code, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<TokenResult> ExchangeIdentityCodeAsync(string code, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<TokenResult> ExchangeCalendarCodeAsync(string code, string scopes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
        {
            RefreshCallCount++;
            LastRefreshToken = refreshToken;
            return Task.FromResult(_refresh(refreshToken));
        }
    }

    private sealed class FakePool : ICalendarAccountStore
    {
        private readonly Dictionary<string, (CalendarAccount account, string? token)> _byId = new(StringComparer.Ordinal);

        public Task<CalendarAccount> AddAsync(CalendarAccount account, string? refreshToken, CancellationToken ct = default)
        {
            _byId[account.Id] = (account, refreshToken);
            return Task.FromResult(account);
        }
        public Task<CalendarAccount?> GetAsync(string accountId, CancellationToken ct = default) =>
            Task.FromResult(_byId.TryGetValue(accountId, out var v) ? v.account : null);
        public Task<IReadOnlyList<CalendarAccount>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarAccount>>(_byId.Values.Select(v => v.account).ToList());
        public Task<string?> GetRefreshTokenAsync(string accountId, CancellationToken ct = default) =>
            Task.FromResult(_byId.TryGetValue(accountId, out var v) ? v.token : null);
        public Task UpdateRefreshTokenAsync(string accountId, string refreshToken, CancellationToken ct = default)
        {
            if (_byId.TryGetValue(accountId, out var v)) _byId[accountId] = (v.account, refreshToken);
            return Task.CompletedTask;
        }
        public Task UpdateStatusAsync(string accountId, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpgradeScopeAsync(string accountId, AccountScope newScope, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(string accountId, CancellationToken ct = default) { _byId.Remove(accountId); return Task.CompletedTask; }
    }

    private sealed class FixedUser : ICurrentUserAccessor
    {
        public FixedUser(string userId) => UserId = userId;
        public string UserId { get; }
    }

    private static IConnectedAccountStore NewLegacy() =>
        new DataProtectionConnectedAccountStore(DataProtectionProvider.Create("tests"));

    private static LegacyConnectedAccountAdapter NewAdapter(ICalendarAccountStore pool, IConnectedAccountStore legacy) =>
        new(pool, legacy, new FixedUser(UserId));

    [Fact]
    public void Ctor_throws_on_null_arguments()
    {
        var tokens = new FakeTokenService(_ => throw new InvalidOperationException());
        var adapter = NewAdapter(new FakePool(), NewLegacy());

        ((Action)(() => new AccountAwareGraphTokenProvider(null!, adapter, "id"))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new AccountAwareGraphTokenProvider(tokens, null!, "id"))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Resolves_token_for_a_pool_account_by_id()
    {
        var pool = new FakePool();
        await pool.AddAsync(new CalendarAccount
        {
            Id = "pool-id", UserId = UserId, Kind = AccountKind.Graph, Provider = "microsoft",
            AccountEmail = "p@test", Scope = AccountScope.ReadWrite, ConnectedAt = DateTimeOffset.UtcNow,
        }, "pool-rt");
        var tokens = new FakeTokenService(rt => new TokenResult
        {
            AccessToken = "access-pool", RefreshToken = rt, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
        });
        var provider = new AccountAwareGraphTokenProvider(tokens, NewAdapter(pool, NewLegacy()), "pool-id");

        var token = await provider.GetAccessTokenAsync();

        token.Should().Be("access-pool");
        tokens.LastRefreshToken.Should().Be("pool-rt");
    }

    [Fact]
    public async Task Resolves_token_for_a_legacy_account_through_the_adapter()
    {
        // The current single-account sync stores its token under the legacy "default" key; a pair
        // wired to that account's accountId must still get its token via the adapter bridge.
        var legacy = NewLegacy();
        await legacy.SetAsync("default", "legacy-default-rt");
        var adapter = NewAdapter(new FakePool(), legacy);
        var legacyId = adapter.DeriveAccountId(UserId, "default");
        var tokens = new FakeTokenService(rt => new TokenResult
        {
            AccessToken = "access-legacy", RefreshToken = rt, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
        });
        var provider = new AccountAwareGraphTokenProvider(tokens, adapter, legacyId);

        var token = await provider.GetAccessTokenAsync();

        token.Should().Be("access-legacy");
        tokens.LastRefreshToken.Should().Be("legacy-default-rt");
    }

    [Fact]
    public async Task Resolves_token_when_given_a_raw_legacy_upn_ref()
    {
        // The factory passes the endpoint's raw AccountRef (a legacy UPN here); the provider must
        // resolve it to the canonical accountId lazily and still fetch the legacy token.
        var legacy = NewLegacy();
        await legacy.SetAsync("solo@test", "solo-rt");
        var adapter = NewAdapter(new FakePool(), legacy);
        var tokens = new FakeTokenService(rt => new TokenResult
        {
            AccessToken = "access-solo", RefreshToken = rt, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
        });
        var provider = new AccountAwareGraphTokenProvider(tokens, adapter, "solo@test");

        (await provider.GetAccessTokenAsync()).Should().Be("access-solo");
        tokens.LastRefreshToken.Should().Be("solo-rt");
    }

    [Fact]
    public async Task Caches_until_expiry_then_force_refresh_bypasses()
    {
        var legacy = NewLegacy();
        await legacy.SetAsync("default", "rt");
        var adapter = NewAdapter(new FakePool(), legacy);
        var id = adapter.DeriveAccountId(UserId, "default");
        var counter = 0;
        var tokens = new FakeTokenService(_ =>
        {
            counter++;
            return new TokenResult { AccessToken = $"access-{counter}", RefreshToken = "rt", ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) };
        });
        var provider = new AccountAwareGraphTokenProvider(tokens, adapter, id);

        var first = await provider.GetAccessTokenAsync();
        var second = await provider.GetAccessTokenAsync();
        var third = await provider.GetAccessTokenAsync(forceRefresh: true);

        first.Should().Be("access-1");
        second.Should().Be("access-1");
        third.Should().Be("access-2");
        tokens.RefreshCallCount.Should().Be(2);
    }

    [Fact]
    public async Task No_connected_account_throws_AuthenticationFailedException()
    {
        var tokens = new FakeTokenService(_ => throw new InvalidOperationException("must not refresh"));
        var provider = new AccountAwareGraphTokenProvider(tokens, NewAdapter(new FakePool(), NewLegacy()), "missing");

        await provider.Invoking(p => p.GetAccessTokenAsync())
            .Should().ThrowAsync<AuthenticationFailedException>();
    }
}
