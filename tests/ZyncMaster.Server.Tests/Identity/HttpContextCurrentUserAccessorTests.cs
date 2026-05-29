using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace ZyncMaster.Server.Tests.Identity;

public class HttpContextCurrentUserAccessorTests
{
    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static HttpContext ContextWithUserClaim(string userId)
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(HttpContextCurrentUserAccessor.UserIdClaimType, userId) }, "Test"));
        return ctx;
    }

    [Fact]
    public void Returns_userId_claim_from_HttpContext()
    {
        var accessor = new FixedHttpContextAccessor { HttpContext = ContextWithUserClaim("u-1") };
        var sut = new HttpContextCurrentUserAccessor(accessor);

        sut.UserId.Should().Be("u-1");
    }

    [Fact]
    public void Falls_back_to_default_when_no_context()
    {
        var sut = new HttpContextCurrentUserAccessor(new FixedHttpContextAccessor { HttpContext = null });

        sut.UserId.Should().Be(DefaultCurrentUserAccessor.DefaultUserId);
    }

    [Fact]
    public void Falls_back_to_default_when_no_claim()
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity());
        var sut = new HttpContextCurrentUserAccessor(new FixedHttpContextAccessor { HttpContext = ctx });

        sut.UserId.Should().Be(DefaultCurrentUserAccessor.DefaultUserId);
    }

    [Fact]
    public void Per_request_override_wins_over_claim()
    {
        var ctx = ContextWithUserClaim("claim-user");
        ctx.Items[HttpContextCurrentUserAccessor.OverrideItemKey] = "override-user";
        var sut = new HttpContextCurrentUserAccessor(new FixedHttpContextAccessor { HttpContext = ctx });

        sut.UserId.Should().Be("override-user");
    }

    [Fact]
    public void Singleton_instance_resolves_different_users_per_ambient_context()
    {
        // Proves the accessor reads the ambient context PER CALL rather than capturing it,
        // so injecting this single instance into the singleton stores is safe: each request
        // (each ambient HttpContext) resolves its own user.
        var accessor = new FixedHttpContextAccessor();
        var sut = new HttpContextCurrentUserAccessor(accessor);

        accessor.HttpContext = ContextWithUserClaim("alice");
        sut.UserId.Should().Be("alice");

        accessor.HttpContext = ContextWithUserClaim("bob");
        sut.UserId.Should().Be("bob");
    }
}
