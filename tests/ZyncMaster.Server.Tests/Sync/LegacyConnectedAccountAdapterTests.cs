using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using ZyncMaster.Core;
using ZyncMaster.Server.Modules.Calendar;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// Unit tests for the §C-3 legacy<->pool bridge. The adapter is the single place that knows both
// account representations; these tests pin the deterministic accountId derivation and the lazy
// on-read fallback from the new pool to the legacy store (no migration).
public sealed class LegacyConnectedAccountAdapterTests
{
    private const string UserId = "user-1";

    // Minimal in-memory pool double scoped to a single user (enough for the bridge tests).
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
            if (_byId.TryGetValue(accountId, out var v))
                _byId[accountId] = (v.account, refreshToken);
            return Task.CompletedTask;
        }

        public Task UpdateStatusAsync(string accountId, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpgradeScopeAsync(string accountId, AccountScope newScope, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string accountId, CancellationToken ct = default)
        {
            _byId.Remove(accountId);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedUser : ICurrentUserAccessor
    {
        public FixedUser(string userId) => UserId = userId;
        public string UserId { get; }
    }

    private static IConnectedAccountStore NewLegacy() =>
        new DataProtectionConnectedAccountStore(DataProtectionProvider.Create("tests"));

    private static LegacyConnectedAccountAdapter NewAdapter(
        ICalendarAccountStore pool, IConnectedAccountStore legacy, string userId = UserId) =>
        new(pool, legacy, new FixedUser(userId));

    [Fact]
    public void DeriveAccountId_is_deterministic_for_same_input()
    {
        var adapter = NewAdapter(new FakePool(), NewLegacy());

        var a = adapter.DeriveAccountId(UserId, "person@test");
        var b = adapter.DeriveAccountId(UserId, "person@test");

        a.Should().Be(b);
        a.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void DeriveAccountId_normalizes_empty_and_default_to_the_same_id()
    {
        var adapter = NewAdapter(new FakePool(), NewLegacy());

        var fromEmpty = adapter.DeriveAccountId(UserId, "");
        var fromNull = adapter.DeriveAccountId(UserId, null);
        var fromDefault = adapter.DeriveAccountId(UserId, "default");

        fromEmpty.Should().Be(fromDefault);
        fromNull.Should().Be(fromDefault);
    }

    [Fact]
    public void DeriveAccountId_differs_per_user_and_per_ref()
    {
        var adapter = NewAdapter(new FakePool(), NewLegacy());

        adapter.DeriveAccountId(UserId, "a@test")
            .Should().NotBe(adapter.DeriveAccountId(UserId, "b@test"));
        adapter.DeriveAccountId("u1", "a@test")
            .Should().NotBe(adapter.DeriveAccountId("u2", "a@test"));
    }

    [Fact]
    public void DeriveAccountId_matches_the_documented_namespace_hash()
    {
        var adapter = NewAdapter(new FakePool(), NewLegacy());

        var expected = UuidV5.Create(
            LegacyConnectedAccountAdapter.AdapterNamespace, $"{UserId}|person@test").ToString("N");

        adapter.DeriveAccountId(UserId, "person@test").Should().Be(expected);
    }

    [Fact]
    public async Task ResolveAsync_returns_pool_account_when_present()
    {
        var pool = new FakePool();
        var pooled = new CalendarAccount
        {
            Id = "pool-id",
            UserId = UserId,
            Kind = AccountKind.Graph,
            Provider = "microsoft",
            AccountEmail = "pooled@test",
            Scope = AccountScope.ReadWrite,
            ConnectedAt = DateTimeOffset.UtcNow,
        };
        await pool.AddAsync(pooled, "pool-rt");
        var adapter = NewAdapter(pool, NewLegacy());

        var resolved = await adapter.ResolveAsync("pool-id");

        resolved.Should().NotBeNull();
        resolved!.AccountEmail.Should().Be("pooled@test");
    }

    [Fact]
    public async Task ResolveAsync_falls_back_to_legacy_account_under_derived_id()
    {
        var legacy = NewLegacy();
        await legacy.SetAsync("legacy@test", "legacy-rt");
        var adapter = NewAdapter(new FakePool(), legacy);
        var derivedId = adapter.DeriveAccountId(UserId, "legacy@test");

        var resolved = await adapter.ResolveAsync(derivedId);

        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be(derivedId);
        resolved.AccountEmail.Should().Be("legacy@test");
        resolved.Kind.Should().Be(AccountKind.Graph);
    }

    [Fact]
    public async Task ResolveAsync_falls_back_to_legacy_default_account()
    {
        var legacy = NewLegacy();
        await legacy.SetAsync("default", "default-rt");
        var adapter = NewAdapter(new FakePool(), legacy);
        var derivedId = adapter.DeriveAccountId(UserId, "default");

        var resolved = await adapter.ResolveAsync(derivedId);

        resolved.Should().NotBeNull();
        resolved!.AccountEmail.Should().BeEmpty();
        resolved.DisplayName.Should().Be("Connected account");
    }

    [Fact]
    public async Task ResolveAsync_returns_null_when_neither_store_has_it()
    {
        var adapter = NewAdapter(new FakePool(), NewLegacy());

        (await adapter.ResolveAsync("nonexistent")).Should().BeNull();
    }

    [Fact]
    public async Task ResolveRefreshTokenAsync_prefers_pool_then_legacy()
    {
        var pool = new FakePool();
        await pool.AddAsync(new CalendarAccount
        {
            Id = "pool-id", UserId = UserId, Kind = AccountKind.Graph, Provider = "microsoft",
            AccountEmail = "p@test", Scope = AccountScope.ReadWrite, ConnectedAt = DateTimeOffset.UtcNow,
        }, "pool-rt");
        var legacy = NewLegacy();
        await legacy.SetAsync("legacy@test", "legacy-rt");
        var adapter = NewAdapter(pool, legacy);

        (await adapter.ResolveRefreshTokenAsync("pool-id")).Should().Be("pool-rt");
        var legacyId = adapter.DeriveAccountId(UserId, "legacy@test");
        (await adapter.ResolveRefreshTokenAsync(legacyId)).Should().Be("legacy-rt");
    }

    [Fact]
    public async Task ResolveAccountIdAsync_returns_pool_id_unchanged_when_real()
    {
        var pool = new FakePool();
        await pool.AddAsync(new CalendarAccount
        {
            Id = "pool-id", UserId = UserId, Kind = AccountKind.Graph, Provider = "microsoft",
            AccountEmail = "p@test", Scope = AccountScope.ReadWrite, ConnectedAt = DateTimeOffset.UtcNow,
        }, "rt");
        var adapter = NewAdapter(pool, NewLegacy());

        (await adapter.ResolveAccountIdAsync("pool-id")).Should().Be("pool-id");
    }

    [Fact]
    public async Task ResolveAccountIdAsync_derives_id_for_a_legacy_ref()
    {
        var adapter = NewAdapter(new FakePool(), NewLegacy());

        var resolved = await adapter.ResolveAccountIdAsync("legacy@test");

        resolved.Should().Be(adapter.DeriveAccountId(UserId, "legacy@test"));
    }

    [Fact]
    public async Task ResolveAccountIdAsync_maps_empty_ref_to_default_derived_id()
    {
        var adapter = NewAdapter(new FakePool(), NewLegacy());

        (await adapter.ResolveAccountIdAsync(""))
            .Should().Be(adapter.DeriveAccountId(UserId, "default"));
    }

    [Fact]
    public async Task UpdateRefreshTokenAsync_rotates_in_the_backing_store()
    {
        var legacy = NewLegacy();
        await legacy.SetAsync("legacy@test", "old-rt");
        var adapter = NewAdapter(new FakePool(), legacy);
        var id = adapter.DeriveAccountId(UserId, "legacy@test");

        await adapter.UpdateRefreshTokenAsync(id, "new-rt");

        (await legacy.GetRefreshTokenAsync("legacy@test")).Should().Be("new-rt");
    }
}
