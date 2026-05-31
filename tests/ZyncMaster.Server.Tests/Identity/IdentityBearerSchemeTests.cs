using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZyncMaster.Server.Data;
using Xunit;

namespace ZyncMaster.Server.Tests.Identity;

// Covers the IdentityBearer authentication scheme that backs GET /api/identity/me (Track A-2,
// body Phase 3). The scheme reads "Authorization: Bearer <token>", validates it with
// IIdentityTokenService.ValidateAccessToken, and stamps the same userId claim the cookie/api-key
// schemes use so ICurrentUserAccessor resolves the caller consistently. Exercised end-to-end
// through WebApplicationFactory so the wiring (Program.cs registration + RequireIdentityBearer)
// is verified, not just the handler in isolation.
public class IdentityBearerSchemeTests
{
    // A mutable clock injected as the host's TimeProvider so token expiry is deterministic:
    // issue a token, advance past its TTL, and the same singleton token service rejects it.
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static WebApplicationFactory<Program> CreateFactory(MutableClock? clock = null) =>
        new ServerTestFactory().WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                if (clock is not null)
                {
                    s.RemoveAll<TimeProvider>();
                    s.AddSingleton<TimeProvider>(clock);
                }
            }));

    // Creates a user (via the multi-provider login upsert) and mints an access token for it,
    // returning the bearer string and the resolved user id.
    private static (string token, string userId) IssueToken(
        WebApplicationFactory<Program> factory, string subject = "bearer-subj", string email = "bearer@example.com")
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var tokens = scope.ServiceProvider.GetRequiredService<IIdentityTokenService>();

        var user = users.UpsertByLoginAsync(
            provider: "local",
            providerSubject: subject,
            email: email,
            emailVerified: true,
            displayName: "Bearer User",
            CancellationToken.None).GetAwaiter().GetResult();

        var issued = tokens.IssueAccessToken(user);
        return (issued.Token, user.Id);
    }

    private static HttpRequestMessage MeRequest(string? bearer)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/identity/me");
        if (bearer is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    [Fact]
    public async Task Me_with_valid_bearer_returns_200_with_user()
    {
        using var factory = CreateFactory();
        var (token, userId) = IssueToken(factory);
        var client = factory.CreateClient();

        var response = await client.SendAsync(MeRequest(token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeResponse>();
        body.Should().NotBeNull();
        body!.UserId.Should().Be(userId);
        body.Email.Should().Be("bearer@example.com");
        body.DisplayName.Should().Be("Bearer User");
    }

    [Fact]
    public async Task Me_without_bearer_returns_401()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(MeRequest(null));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_with_malformed_authorization_header_returns_401()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/identity/me");
        req.Headers.TryAddWithoutValidation("Authorization", "NotBearer abc.def");

        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_with_tampered_bearer_returns_401()
    {
        using var factory = CreateFactory();
        var (token, _) = IssueToken(factory);
        var chars = token.ToCharArray();
        var mid = chars.Length / 2;
        chars[mid] = chars[mid] == 'A' ? 'B' : 'A';
        var tampered = new string(chars);

        var client = factory.CreateClient();
        var response = await client.SendAsync(MeRequest(tampered));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_with_garbage_bearer_returns_401()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(MeRequest("not-a-real-token"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_with_revoked_jti_returns_401()
    {
        using var factory = CreateFactory();
        var (token, _) = IssueToken(factory, subject: "revoked-subj", email: "revoked@example.com");

        using (var scope = factory.Services.CreateScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<IIdentityTokenService>();
            var principal = tokens.ValidateAccessToken(token);
            principal.Should().NotBeNull();
            await tokens.RevokeAccessAsync(principal!.Jti);
        }

        var client = factory.CreateClient();
        var response = await client.SendAsync(MeRequest(token));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_with_expired_token_returns_401()
    {
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        using var factory = CreateFactory(clock);
        var (token, _) = IssueToken(factory, subject: "expired-subj", email: "expired@example.com");

        // Token is valid right now.
        var client = factory.CreateClient();
        (await client.SendAsync(MeRequest(token))).StatusCode.Should().Be(HttpStatusCode.OK);

        // Advance well past the access-token TTL; the same singleton token service now rejects it.
        clock.Advance(TimeSpan.FromDays(365));

        var response = await client.SendAsync(MeRequest(token));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CurrentUserAccessor_resolves_token_user_id_on_identity_bearer_request()
    {
        // /api/identity/me reads its identity from ICurrentUserAccessor, which resolves the
        // userId claim stamped by the IdentityBearer handler. A correct userId in the response
        // is direct evidence that the accessor resolved the token's user for the request.
        using var factory = CreateFactory();
        var (token, userId) = IssueToken(factory, subject: "accessor-subj", email: "accessor@example.com");
        var client = factory.CreateClient();

        var response = await client.SendAsync(MeRequest(token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeResponse>();
        body!.UserId.Should().Be(userId);
    }

    private sealed record MeResponse(string UserId, string Email, string? DisplayName, string? Plan);
}
