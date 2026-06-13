using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ZyncMaster.Server.Tests.Identity;

public class IdentityConnectEndpointsTests
{
    private const string IdentityStateCookieName = "sm_identity_oauth_state";

    // The fixed tenant id Microsoft uses for ALL personal accounts (MSA / consumers).
    private const string MsaTenantId = "9188040d-6c67-4c5b-b112-36a304b66dad";

    // Fake token service whose ExchangeIdentityCodeAsync returns a fixed identity. The legacy
    // ExchangeCodeAsync/RefreshAsync are not exercised by the identity flow. The account-linking
    // trust signals (email_verified / tid / xms_edov) are settable so the invariant tests can
    // drive verified vs unverified Microsoft sign-ins through the full HTTP flow.
    private sealed class FakeTokenService : IMicrosoftTokenService
    {
        public string? Subject { get; init; } = "oid-id";
        public string Upn { get; init; } = "id@test";
        public string? Name { get; init; } = "Identity Tester";
        public bool? EmailVerified { get; init; }
        public string? TenantId { get; init; } = MsaTenantId;
        public bool? EmailDomainOwnerVerified { get; init; }

        public Task<TokenResult> ExchangeIdentityCodeAsync(string code, CancellationToken ct = default) =>
            Task.FromResult(new TokenResult
            {
                AccessToken = "calendar-at-should-not-be-used",
                RefreshToken = "calendar-rt-should-not-be-stored",
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
                UserPrincipalName = Upn,
                Subject = Subject,
                Email = Upn,
                DisplayName = Name,
                EmailVerified = EmailVerified,
                TenantId = TenantId,
                EmailDomainOwnerVerified = EmailDomainOwnerVerified,
            });

        public Task<TokenResult> ExchangeCodeAsync(string code, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TokenResult> ExchangeCalendarCodeAsync(
            string code, string scopes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static WebApplicationFactory<Program> CreateFactory(FakeTokenService? fake = null) =>
        new ServerTestFactory().WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IMicrosoftTokenService>();
                s.AddSingleton<IMicrosoftTokenService>(fake ?? new FakeTokenService());
            }));

    private static HttpClient NoRedirectClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string ExtractCookie(HttpResponseMessage response, string name)
    {
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith(name + "=", StringComparison.Ordinal));
        return setCookie.Split(';')[0];
    }

    private static string ExtractQueryValue(Uri location, string key)
    {
        var query = location.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (Uri.UnescapeDataString(pair[..idx]) == key)
                return Uri.UnescapeDataString(pair[(idx + 1)..]);
        }
        throw new InvalidOperationException($"{key} not found in {location}");
    }

    [Fact]
    public async Task Connect_redirects_to_authorize_with_identity_scopes_state_and_csrf_cookie()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync("/identity/connect/microsoft?port=51789&nonce=app-nonce-1");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!.ToString();
        location.Should().Contain("/authorize");
        location.Should().Contain("response_type=code");
        location.Should().Contain("prompt=select_account");
        location.Should().Contain("state=");

        // Identity scopes (openid email profile), NOT calendar scopes.
        var scope = ExtractQueryValue(resp.Headers.Location!, "scope");
        scope.Should().Contain("openid");
        scope.Should().Contain("email");
        scope.Should().Contain("profile");
        scope.Should().NotContain("Calendars");

        // CSRF cookie set.
        resp.Headers.GetValues("Set-Cookie")
            .Any(c => c.StartsWith(IdentityStateCookieName + "=", StringComparison.Ordinal))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("80", "app-nonce")]      // port below 1024
    [InlineData("70000", "app-nonce")]   // port above 65535
    [InlineData("notanumber", "app-nonce")]
    [InlineData("51789", "")]            // empty nonce
    public async Task Connect_rejects_bad_port_or_nonce(string port, string nonce)
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync(
            $"/identity/connect/microsoft?port={Uri.EscapeDataString(port)}&nonce={Uri.EscapeDataString(nonce)}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_happy_path_upserts_user_and_redirects_to_loopback_with_handle()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var connect = await client.GetAsync("/identity/connect/microsoft?port=51790&nonce=nonce-xyz");
        var state = ExtractQueryValue(connect.Headers.Location!, "state");
        var csrfCookie = ExtractCookie(connect, IdentityStateCookieName);

        var callback = new HttpRequestMessage(
            HttpMethod.Get,
            $"/identity/connect/callback/microsoft?code=abc&state={Uri.EscapeDataString(state)}");
        callback.Headers.Add("Cookie", csrfCookie);

        var resp = await client.SendAsync(callback);

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!;
        location.Scheme.Should().Be("http");
        location.Host.Should().Be("127.0.0.1");
        location.Port.Should().Be(51790);
        location.AbsolutePath.Should().Be("/identity/callback");
        ExtractQueryValue(location, "nonce").Should().Be("nonce-xyz");
        var handle = ExtractQueryValue(location, "handle");
        handle.Should().NotBeNullOrEmpty();

        // The user (and an IdentityLogin) was upserted from the id_token identity.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMaster.Server.Data.ZyncMasterDbContext>();
        db.Users.Any(u => u.Provider == "microsoft" && u.Subject == "oid-id").Should().BeTrue();
        db.IdentityLogins.Any(l => l.Provider == "microsoft" && l.ProviderSubject == "oid-id").Should().BeTrue();

        // NO calendar account was connected by this identity flow.
        db.ConnectedAccounts.Any().Should().BeFalse();
    }

    // ----- Cross-provider linking security invariants (through the full HTTP flow) ----------

    // Drives one Microsoft sign-in callback to completion and returns the resolved userId.
    private static async Task<string> CompleteMicrosoftSignInAsync(
        WebApplicationFactory<Program> factory, HttpClient client, int port, string subject)
    {
        var connect = await client.GetAsync($"/identity/connect/microsoft?port={port}&nonce=n-{port}");
        var state = ExtractQueryValue(connect.Headers.Location!, "state");
        var csrfCookie = ExtractCookie(connect, IdentityStateCookieName);

        var callback = new HttpRequestMessage(
            HttpMethod.Get,
            $"/identity/connect/callback/microsoft?code=abc&state={Uri.EscapeDataString(state)}");
        callback.Headers.Add("Cookie", csrfCookie);
        var resp = await client.SendAsync(callback);
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMaster.Server.Data.ZyncMasterDbContext>();
        var login = db.IdentityLogins.Single(l => l.Provider == "microsoft" && l.ProviderSubject == subject);
        return login.UserId;
    }

    // INVARIANT 1: a Microsoft sign-in whose verified email matches no existing user creates a
    // normal new user.
    [Fact]
    public async Task Invariant_microsoft_signin_with_no_matching_user_creates_new_user()
    {
        using var factory = CreateFactory(new FakeTokenService
        {
            Subject = "ms-new",
            Upn = "fresh@test",
            Name = "Fresh",
            TenantId = MsaTenantId, // MSA personal => email is verified identity
        });
        var client = NoRedirectClient(factory);

        var userId = await CompleteMicrosoftSignInAsync(factory, client, 52001, "ms-new");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMaster.Server.Data.ZyncMasterDbContext>();
        var user = db.Users.Single(u => u.Id == userId);
        user.PrimaryEmail.Should().Be("fresh@test");
        // A normal standalone user with exactly its own one login.
        db.IdentityLogins.Count(l => l.UserId == userId).Should().Be(1);
    }

    // INVARIANT 2: a Microsoft sign-in whose VERIFIED email matches an existing magic-link user
    // resolves to that SAME UserId (so it inherits that user's pairs/devices).
    [Fact]
    public async Task Invariant_verified_microsoft_signin_links_to_existing_magiclink_user()
    {
        using var factory = CreateFactory(new FakeTokenService
        {
            Subject = "ms-link",
            Upn = "owner@test",
            Name = "Owner MS",
            TenantId = MsaTenantId,
        });
        var client = NoRedirectClient(factory);

        // Pre-seed the magic-link (local, verified) user that owns the data + a device.
        string magicUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IUserStore>();
            var magicUser = await store.UpsertByLoginAsync("local", "owner@test", "owner@test", true, "Owner");
            magicUserId = magicUser.Id;
            var db = scope.ServiceProvider.GetRequiredService<ZyncMaster.Server.Data.ZyncMasterDbContext>();
            db.Devices.Add(NewDevice(magicUserId));
            await db.SaveChangesAsync();
        }

        var resolvedId = await CompleteMicrosoftSignInAsync(factory, client, 52002, "ms-link");

        resolvedId.Should().Be(magicUserId);

        using var verify = factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<ZyncMaster.Server.Data.ZyncMasterDbContext>();
        vdb.IdentityLogins.Count(l => l.UserId == magicUserId).Should().Be(2);
        vdb.Devices.Count(d => d.UserId == magicUserId).Should().Be(1);
    }

    // INVARIANT 3: a Microsoft sign-in on an UNVERIFIED email (explicit email_verified=false) must
    // NOT link to a pre-existing magic-link user sharing that address.
    [Fact]
    public async Task Invariant_unverified_microsoft_signin_does_not_link_to_existing_user()
    {
        using var factory = CreateFactory(new FakeTokenService
        {
            Subject = "ms-unverified",
            Upn = "owner@test",
            Name = "Owner MS",
            EmailVerified = false, // explicit false blocks any linking
            TenantId = MsaTenantId,
        });
        var client = NoRedirectClient(factory);

        string magicUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IUserStore>();
            magicUserId = (await store.UpsertByLoginAsync("local", "owner@test", "owner@test", true, "Owner")).Id;
        }

        var resolvedId = await CompleteMicrosoftSignInAsync(factory, client, 52003, "ms-unverified");

        resolvedId.Should().NotBe(magicUserId);

        using var verify = factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<ZyncMaster.Server.Data.ZyncMasterDbContext>();
        // The magic-link user keeps exactly its own one login.
        vdb.IdentityLogins.Count(l => l.UserId == magicUserId).Should().Be(1);
    }

    // INVARIANT 3b (nOAuth): a work/school (AAD) Microsoft sign-in WITHOUT xms_edov and WITHOUT an
    // explicit email_verified must NOT be trusted — the raw email claim is spoofable, so it must
    // not link to a pre-existing user with the same address.
    [Fact]
    public async Task Invariant_aad_signin_without_edov_does_not_link_to_existing_user()
    {
        using var factory = CreateFactory(new FakeTokenService
        {
            Subject = "aad-noedov",
            Upn = "owner@test",
            Name = "Attacker",
            EmailVerified = null,             // no explicit claim
            TenantId = "some-org-tenant-id",  // an AAD tenant, NOT the MSA consumer tenant
            EmailDomainOwnerVerified = null,  // no domain-owner-verified signal
        });
        var client = NoRedirectClient(factory);

        string magicUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IUserStore>();
            magicUserId = (await store.UpsertByLoginAsync("local", "owner@test", "owner@test", true, "Owner")).Id;
        }

        var resolvedId = await CompleteMicrosoftSignInAsync(factory, client, 52004, "aad-noedov");

        resolvedId.Should().NotBe(magicUserId);

        using var verify = factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<ZyncMaster.Server.Data.ZyncMasterDbContext>();
        vdb.IdentityLogins.Count(l => l.UserId == magicUserId).Should().Be(1);
    }

    // INVARIANT 3c (nOAuth — the takeover case): a work/school (AAD) sign-in that emits
    // email_verified=TRUE for a victim's address but carries NEITHER the MSA consumer tenant NOR
    // xms_edov must NOT link. An attacker can stand up a free Entra tenant and emit an arbitrary
    // email + email_verified, so a bare email_verified=true is NOT authoritative — it must be paired
    // with a Microsoft issuer signal. This is the boundary a verbatim "trust email_verified" would
    // have crossed (account takeover).
    [Fact]
    public async Task Invariant_aad_signin_with_forged_email_verified_true_but_no_issuer_signal_does_not_link()
    {
        using var factory = CreateFactory(new FakeTokenService
        {
            Subject = "aad-forged",
            Upn = "owner@test",
            Name = "Attacker",
            EmailVerified = true,             // attacker-emitted, NOT trustworthy on its own
            TenantId = "attacker-org-tenant", // an arbitrary AAD tenant, NOT the MSA consumer tenant
            EmailDomainOwnerVerified = null,  // no xms_edov: Microsoft does NOT vouch for the domain
        });
        var client = NoRedirectClient(factory);

        string magicUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IUserStore>();
            magicUserId = (await store.UpsertByLoginAsync("local", "owner@test", "owner@test", true, "Owner")).Id;
        }

        var resolvedId = await CompleteMicrosoftSignInAsync(factory, client, 52005, "aad-forged");

        // The attacker landed on a brand-new separate user, NOT the victim's account.
        resolvedId.Should().NotBe(magicUserId);

        using var verify = factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<ZyncMaster.Server.Data.ZyncMasterDbContext>();
        vdb.IdentityLogins.Count(l => l.UserId == magicUserId).Should().Be(1);
    }

    // INVARIANT 4 (orphan repoint, end-to-end): an ALREADY-created Microsoft (provider,subject)
    // login that currently resolves to an empty orphan user gets repointed to the verified-email
    // magic-link user on the next sign-in, and the empty orphan is removed.
    [Fact]
    public async Task Invariant_existing_orphan_microsoft_login_repoints_to_verified_email_user()
    {
        using var factory = CreateFactory(new FakeTokenService
        {
            Subject = "ms-orphan",
            Upn = "owner@test",
            Name = "Owner MS",
            TenantId = MsaTenantId,
        });
        var client = NoRedirectClient(factory);

        string magicUserId;
        string orphanId;
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IUserStore>();
            var magicUser = await store.UpsertByLoginAsync("local", "owner@test", "owner@test", true, "Owner");
            magicUserId = magicUser.Id;

            var db = scope.ServiceProvider.GetRequiredService<ZyncMaster.Server.Data.ZyncMasterDbContext>();
            db.Devices.Add(NewDevice(magicUserId));

            // The empty orphan minted by the OLD Microsoft flow.
            var orphan = new ZyncMaster.Server.Data.UserRow
            {
                Id = Guid.NewGuid().ToString("N"),
                Provider = "microsoft",
                Subject = "ms-orphan",
                Email = "owner@test",
                DisplayName = "Owner MS",
                CreatedUtc = DateTimeOffset.UtcNow,
                PrimaryEmail = "owner@test",
            };
            orphanId = orphan.Id;
            db.Users.Add(orphan);
            db.IdentityLogins.Add(new ZyncMaster.Server.Data.IdentityLoginRow
            {
                Id = Guid.NewGuid().ToString("N"),
                UserId = orphan.Id,
                Provider = "microsoft",
                ProviderSubject = "ms-orphan",
                Email = "owner@test",
                EmailVerified = false,
                LinkedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resolvedId = await CompleteMicrosoftSignInAsync(factory, client, 52005, "ms-orphan");

        resolvedId.Should().Be(magicUserId);

        using var verify = factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<ZyncMaster.Server.Data.ZyncMasterDbContext>();
        // The Microsoft login now points at the data-owning user.
        vdb.IdentityLogins.Single(l => l.ProviderSubject == "ms-orphan").UserId.Should().Be(magicUserId);
        vdb.IdentityLogins.Count(l => l.UserId == magicUserId).Should().Be(2);
        // The empty orphan user was removed.
        vdb.Users.Any(u => u.Id == orphanId).Should().BeFalse();
        // The device survived on the canonical user.
        vdb.Devices.Count(d => d.UserId == magicUserId).Should().Be(1);
    }

    private static ZyncMaster.Server.Data.DeviceRow NewDevice(string userId)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        return new()
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            Name = "Box-" + suffix,
            NameLower = ("box-" + suffix).ToLowerInvariant(),
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTimeOffset.UtcNow,
            LastSeenUtc = DateTimeOffset.UtcNow,
        };
    }

    [Fact]
    public async Task Callback_with_tampered_state_returns_400()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var connect = await client.GetAsync("/identity/connect/microsoft?port=51791&nonce=n");
        var csrfCookie = ExtractCookie(connect, IdentityStateCookieName);

        var callback = new HttpRequestMessage(
            HttpMethod.Get, "/identity/connect/callback/microsoft?code=abc&state=tampered-blob");
        callback.Headers.Add("Cookie", csrfCookie);

        var resp = await client.SendAsync(callback);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_with_mismatched_csrf_cookie_returns_400()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        // Two independent connect calls produce two different csrf values; pairing the state
        // of one with the cookie of the other must be rejected.
        var connectA = await client.GetAsync("/identity/connect/microsoft?port=51792&nonce=a");
        var stateA = ExtractQueryValue(connectA.Headers.Location!, "state");

        var connectB = await client.GetAsync("/identity/connect/microsoft?port=51792&nonce=b");
        var csrfCookieB = ExtractCookie(connectB, IdentityStateCookieName);

        var callback = new HttpRequestMessage(
            HttpMethod.Get,
            $"/identity/connect/callback/microsoft?code=abc&state={Uri.EscapeDataString(stateA)}");
        callback.Headers.Add("Cookie", csrfCookieB);

        var resp = await client.SendAsync(callback);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_with_error_returns_html()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync(
            "/identity/connect/callback/microsoft?error=access_denied");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task Redeem_returns_tokens_then_is_single_use()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var connect = await client.GetAsync("/identity/connect/microsoft?port=51793&nonce=rdm");
        var state = ExtractQueryValue(connect.Headers.Location!, "state");
        var csrfCookie = ExtractCookie(connect, IdentityStateCookieName);

        var callbackReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"/identity/connect/callback/microsoft?code=abc&state={Uri.EscapeDataString(state)}");
        callbackReq.Headers.Add("Cookie", csrfCookie);
        var callback = await client.SendAsync(callbackReq);
        var handle = ExtractQueryValue(callback.Headers.Location!, "handle");

        var first = await client.PostAsJsonAsync("/identity/handle/redeem", new { handle });
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var accessToken = doc.RootElement.GetProperty("accessToken").GetString();
        var refreshToken = doc.RootElement.GetProperty("refreshToken").GetString();
        accessToken.Should().NotBeNullOrEmpty();
        refreshToken.Should().NotBeNullOrEmpty();

        // The access token validates against the identity token service.
        var tokens = factory.Services.GetRequiredService<IIdentityTokenService>();
        tokens.ValidateAccessToken(accessToken!).Should().NotBeNull();

        // Second redeem of the same handle fails (one-time).
        var second = await client.PostAsJsonAsync("/identity/handle/redeem", new { handle });
        ((int)second.StatusCode).Should().BeOneOf(400, 410);
    }

    [Fact]
    public async Task Redeem_unknown_handle_fails()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var resp = await client.PostAsJsonAsync(
            "/identity/handle/redeem", new { handle = "does-not-exist" });

        ((int)resp.StatusCode).Should().BeOneOf(400, 410);
    }

    [Fact]
    public async Task Me_with_valid_bearer_returns_profile()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var connect = await client.GetAsync("/identity/connect/microsoft?port=51794&nonce=me");
        var state = ExtractQueryValue(connect.Headers.Location!, "state");
        var csrfCookie = ExtractCookie(connect, IdentityStateCookieName);

        var callbackReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"/identity/connect/callback/microsoft?code=abc&state={Uri.EscapeDataString(state)}");
        callbackReq.Headers.Add("Cookie", csrfCookie);
        var callback = await client.SendAsync(callbackReq);
        var handle = ExtractQueryValue(callback.Headers.Location!, "handle");

        var redeem = await client.PostAsJsonAsync("/identity/handle/redeem", new { handle });
        using var redeemDoc = JsonDocument.Parse(await redeem.Content.ReadAsStringAsync());
        var accessToken = redeemDoc.RootElement.GetProperty("accessToken").GetString();

        var meReq = new HttpRequestMessage(HttpMethod.Get, "/api/identity/me");
        meReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var me = await client.SendAsync(meReq);

        me.StatusCode.Should().Be(HttpStatusCode.OK);
        using var meDoc = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        meDoc.RootElement.GetProperty("email").GetString().Should().Be("id@test");
        meDoc.RootElement.GetProperty("displayName").GetString().Should().Be("Identity Tester");
        meDoc.RootElement.TryGetProperty("userId", out _).Should().BeTrue();
        meDoc.RootElement.TryGetProperty("plan", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Me_without_bearer_returns_401()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync("/api/identity/me");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_with_invalid_bearer_returns_401()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var meReq = new HttpRequestMessage(HttpMethod.Get, "/api/identity/me");
        meReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "garbage-token");
        var resp = await client.SendAsync(meReq);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
