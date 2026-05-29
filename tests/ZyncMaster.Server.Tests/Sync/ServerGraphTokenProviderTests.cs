using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

public class ServerGraphTokenProviderTests
{
    private sealed class FakeTokenService : IMicrosoftTokenService
    {
        private readonly Func<string, TokenResult> _refresh;
        public int RefreshCallCount { get; private set; }
        public string? LastRefreshToken { get; private set; }

        public FakeTokenService(Func<string, TokenResult> refresh) => _refresh = refresh;

        public Task<TokenResult> ExchangeCodeAsync(string code, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
        {
            RefreshCallCount++;
            LastRefreshToken = refreshToken;
            return Task.FromResult(_refresh(refreshToken));
        }
    }

    private static IConnectedAccountStore NewStore() =>
        new DataProtectionConnectedAccountStore(DataProtectionProvider.Create("tests"));

    [Fact]
    public async Task Ctor_throws_on_null_arguments()
    {
        var tokens = new FakeTokenService(_ => throw new InvalidOperationException());
        var accounts = NewStore();

        Action a = () => new ServerGraphTokenProvider(null!, accounts, "");
        Action b = () => new ServerGraphTokenProvider(tokens, null!, "");
        Action c = () => new ServerGraphTokenProvider(tokens, accounts, null!);

        a.Should().Throw<ArgumentNullException>();
        b.Should().Throw<ArgumentNullException>();
        c.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Refreshes_using_stored_refresh_token()
    {
        var tokens = new FakeTokenService(rt => new TokenResult
        {
            AccessToken = "access-1",
            RefreshToken = rt,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
        });
        var accounts = NewStore();
        await accounts.SetAsync("user", "stored-refresh");

        var provider = new ServerGraphTokenProvider(tokens, accounts, "user");
        var token = await provider.GetAccessTokenAsync();

        token.Should().Be("access-1");
        tokens.RefreshCallCount.Should().Be(1);
        tokens.LastRefreshToken.Should().Be("stored-refresh");
    }

    [Fact]
    public async Task Caches_until_expiry_so_second_call_does_not_refresh()
    {
        var tokens = new FakeTokenService(_ => new TokenResult
        {
            AccessToken = "access-1",
            RefreshToken = "stored-refresh",
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
        });
        var accounts = NewStore();
        await accounts.SetAsync("user", "stored-refresh");

        var provider = new ServerGraphTokenProvider(tokens, accounts, "user");
        var first = await provider.GetAccessTokenAsync();
        var second = await provider.GetAccessTokenAsync();

        first.Should().Be("access-1");
        second.Should().Be("access-1");
        tokens.RefreshCallCount.Should().Be(1);
    }

    [Fact]
    public async Task ForceRefresh_bypasses_cache()
    {
        var counter = 0;
        var tokens = new FakeTokenService(_ =>
        {
            counter++;
            return new TokenResult
            {
                AccessToken = $"access-{counter}",
                RefreshToken = "stored-refresh",
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
            };
        });
        var accounts = NewStore();
        await accounts.SetAsync("user", "stored-refresh");

        var provider = new ServerGraphTokenProvider(tokens, accounts, "user");
        var first = await provider.GetAccessTokenAsync();
        var second = await provider.GetAccessTokenAsync(forceRefresh: true);

        first.Should().Be("access-1");
        second.Should().Be("access-2");
        tokens.RefreshCallCount.Should().Be(2);
    }

    [Fact]
    public async Task No_connected_account_throws_AuthenticationFailedException()
    {
        var tokens = new FakeTokenService(_ => throw new InvalidOperationException("must not refresh"));
        var accounts = NewStore();

        var provider = new ServerGraphTokenProvider(tokens, accounts, "user");
        Func<Task> act = () => provider.GetAccessTokenAsync();

        await act.Should().ThrowAsync<AuthenticationFailedException>()
            .WithMessage("No Microsoft account is connected.");
        tokens.RefreshCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Rotated_refresh_token_is_persisted()
    {
        var tokens = new FakeTokenService(_ => new TokenResult
        {
            AccessToken = "access-1",
            RefreshToken = "rotated-refresh",
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
        });
        var accounts = NewStore();
        await accounts.SetAsync("user", "stored-refresh");

        var provider = new ServerGraphTokenProvider(tokens, accounts, "user");
        await provider.GetAccessTokenAsync();

        var persisted = await accounts.GetRefreshTokenAsync("user");
        persisted.Should().Be("rotated-refresh");
    }

    [Fact]
    public async Task Unchanged_refresh_token_is_not_rewritten()
    {
        var tokens = new FakeTokenService(_ => new TokenResult
        {
            AccessToken = "access-1",
            RefreshToken = "stored-refresh",
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
        });
        var accounts = NewStore();
        await accounts.SetAsync("user", "stored-refresh");

        var provider = new ServerGraphTokenProvider(tokens, accounts, "user");
        await provider.GetAccessTokenAsync();

        var persisted = await accounts.GetRefreshTokenAsync("user");
        persisted.Should().Be("stored-refresh");
    }
}
