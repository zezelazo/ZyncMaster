using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Server.Tests.Calendar;

// Unit tests for CalendarAccountEmailBackfill — the best-effort, one-time fill of a connected
// account's blank email/displayName via refresh-token + Graph /me, used so accounts connected
// before /me capture existed stop showing the internal accountRef GUID in the UI.
public class CalendarAccountEmailBackfillTests
{
    // Minimal in-memory account store: only the members the backfill touches do real work.
    private sealed class FakeStore : ICalendarAccountStore
    {
        public string? RefreshToken { get; set; } = "rt-1";
        public string? PersistedEmail { get; private set; }
        public string? PersistedDisplayName { get; private set; }
        public int ProfileUpdates { get; private set; }
        public string? RotatedRefreshToken { get; private set; }

        public Task<string?> GetRefreshTokenAsync(string accountId, CancellationToken ct = default) =>
            Task.FromResult(RefreshToken);

        public Task UpdateRefreshTokenAsync(string accountId, string refreshToken, CancellationToken ct = default)
        {
            RotatedRefreshToken = refreshToken;
            RefreshToken = refreshToken;
            return Task.CompletedTask;
        }

        public Task UpdateProfileAsync(string accountId, string? email, string? displayName, CancellationToken ct = default)
        {
            ProfileUpdates++;
            if (!string.IsNullOrWhiteSpace(email)) PersistedEmail = email;
            if (!string.IsNullOrWhiteSpace(displayName)) PersistedDisplayName = displayName;
            return Task.CompletedTask;
        }

        public Task<CalendarAccount> AddAsync(CalendarAccount account, string? refreshToken, CancellationToken ct = default) =>
            Task.FromResult(account);
        public Task<CalendarAccount?> GetAsync(string accountId, CancellationToken ct = default) =>
            Task.FromResult<CalendarAccount?>(null);
        public Task<IReadOnlyList<CalendarAccount>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarAccount>>(Array.Empty<CalendarAccount>());
        public Task UpdateStatusAsync(string accountId, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpgradeScopeAsync(string accountId, AccountScope newScope, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(string accountId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeTokens : IMicrosoftTokenService
    {
        public string AccessToken { get; init; } = "access-1";
        public string ReturnedRefreshToken { get; init; } = "rt-1";
        public bool Throw { get; init; }
        public int RefreshCalls { get; private set; }

        public Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
        {
            RefreshCalls++;
            if (Throw) throw new InvalidOperationException("refresh failed");
            return Task.FromResult(new TokenResult
            {
                AccessToken = AccessToken,
                RefreshToken = ReturnedRefreshToken,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
            });
        }

        public Task<TokenResult> ExchangeCodeAsync(string code, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TokenResult> ExchangeIdentityCodeAsync(string code, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TokenResult> ExchangeCalendarCodeAsync(string code, string scopes, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeUserInfo : IGraphUserInfoService
    {
        public GraphUserInfo Result { get; init; } = new("me@contoso.com", "Me");
        public int Calls { get; private set; }
        public Task<GraphUserInfo> GetMeAsync(string accessToken, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private static CalendarAccount Account(AccountKind kind = AccountKind.Graph, string email = "") => new()
    {
        Id = "acc-1",
        UserId = "user-1",
        Kind = kind,
        Provider = "microsoft",
        AccountEmail = email,
        Scope = AccountScope.ReadWrite,
        DisplayName = "",
        Status = "active",
        ConnectedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task EnsureEmail_backfills_and_persists_when_email_is_blank()
    {
        var store = new FakeStore();
        var tokens = new FakeTokens();
        var me = new FakeUserInfo { Result = new GraphUserInfo("real@contoso.com", "Real") };
        var backfill = new CalendarAccountEmailBackfill(store, tokens, me);

        var result = await backfill.EnsureEmailAsync(Account());

        result.AccountEmail.Should().Be("real@contoso.com");
        result.DisplayName.Should().Be("Real");
        store.PersistedEmail.Should().Be("real@contoso.com");
        store.PersistedDisplayName.Should().Be("Real");
        me.Calls.Should().Be(1);
    }

    [Fact]
    public async Task EnsureEmail_skips_account_that_already_has_email()
    {
        var store = new FakeStore();
        var tokens = new FakeTokens();
        var me = new FakeUserInfo();
        var backfill = new CalendarAccountEmailBackfill(store, tokens, me);

        var result = await backfill.EnsureEmailAsync(Account(email: "already@contoso.com"));

        result.AccountEmail.Should().Be("already@contoso.com");
        tokens.RefreshCalls.Should().Be(0);
        me.Calls.Should().Be(0);
        store.ProfileUpdates.Should().Be(0);
    }

    [Fact]
    public async Task EnsureEmail_skips_non_graph_account()
    {
        var store = new FakeStore();
        var tokens = new FakeTokens();
        var me = new FakeUserInfo();
        var backfill = new CalendarAccountEmailBackfill(store, tokens, me);

        var result = await backfill.EnsureEmailAsync(Account(kind: AccountKind.OutlookCom));

        result.AccountEmail.Should().BeEmpty();
        tokens.RefreshCalls.Should().Be(0);
        me.Calls.Should().Be(0);
    }

    [Fact]
    public async Task EnsureEmail_returns_account_unchanged_when_no_refresh_token()
    {
        var store = new FakeStore { RefreshToken = null };
        var tokens = new FakeTokens();
        var me = new FakeUserInfo();
        var backfill = new CalendarAccountEmailBackfill(store, tokens, me);

        var result = await backfill.EnsureEmailAsync(Account());

        result.AccountEmail.Should().BeEmpty();
        tokens.RefreshCalls.Should().Be(0);
        store.ProfileUpdates.Should().Be(0);
    }

    [Fact]
    public async Task EnsureEmail_returns_account_unchanged_when_refresh_throws()
    {
        var store = new FakeStore();
        var tokens = new FakeTokens { Throw = true };
        var me = new FakeUserInfo();
        var backfill = new CalendarAccountEmailBackfill(store, tokens, me);

        var result = await backfill.EnsureEmailAsync(Account());

        result.AccountEmail.Should().BeEmpty();
        store.ProfileUpdates.Should().Be(0);
        me.Calls.Should().Be(0);
    }

    [Fact]
    public async Task EnsureEmail_rotates_refresh_token_when_a_new_one_is_returned()
    {
        var store = new FakeStore { RefreshToken = "old-rt" };
        var tokens = new FakeTokens { ReturnedRefreshToken = "new-rt" };
        var me = new FakeUserInfo();
        var backfill = new CalendarAccountEmailBackfill(store, tokens, me);

        await backfill.EnsureEmailAsync(Account());

        store.RotatedRefreshToken.Should().Be("new-rt");
    }

    [Fact]
    public async Task EnsureEmail_does_not_persist_when_me_returns_nothing()
    {
        var store = new FakeStore();
        var tokens = new FakeTokens();
        var me = new FakeUserInfo { Result = GraphUserInfo.Empty };
        var backfill = new CalendarAccountEmailBackfill(store, tokens, me);

        var result = await backfill.EnsureEmailAsync(Account());

        result.AccountEmail.Should().BeEmpty();
        store.ProfileUpdates.Should().Be(0);
    }
}
