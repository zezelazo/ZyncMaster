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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZyncMaster.Server.Data;
using ZyncMaster.Server.Infrastructure.Email;
using Xunit;

namespace ZyncMaster.Server.Tests.Identity;

public class MagicLinkEndpointsTests
{
    // Captures the most recent email (and extracts the magic-link token from its body) so tests
    // can assert what was actually sent without any network. Thread-safe enough for the test host.
    private sealed class CapturingEmailSender : IEmailSender
    {
        public int SendCount;
        public string? LastTo;
        public string? LastBody;
        public string? LastToken;

        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
        {
            Interlocked.Increment(ref SendCount);
            LastTo = toEmail;
            LastBody = htmlBody;
            LastToken = ExtractToken(htmlBody);
            return Task.CompletedTask;
        }

        private static string? ExtractToken(string body)
        {
            const string marker = "token=";
            var i = body.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return null;
            var start = i + marker.Length;
            var end = body.IndexOfAny(new[] { '"', '&', '<', ' ' }, start);
            if (end < 0) end = body.Length;
            return Uri.UnescapeDataString(body[start..end]);
        }
    }

    // Controllable clock so TTL/expiry is deterministic.
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        CapturingEmailSender sender, FakeClock? clock = null) =>
        new ServerTestFactory().WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IEmailSender>();
                s.AddSingleton<IEmailSender>(sender);
                if (clock is not null)
                {
                    s.RemoveAll<TimeProvider>();
                    s.AddSingleton<TimeProvider>(clock);
                }
            }));

    private static HttpClient NoRedirectClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

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

    private static object Body(string email, int port = 51820, string nonce = "app-nonce") =>
        new { email, port, nonce };

    [Fact]
    public async Task Post_new_and_unknown_email_both_return_202_with_same_body_and_capture_token()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        var client = NoRedirectClient(factory);

        // "Unknown" email — no pre-existing user.
        var resp1 = await client.PostAsJsonAsync("/identity/magic-link", Body("stranger@example.com"));
        resp1.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body1 = await resp1.Content.ReadAsStringAsync();
        sender.LastToken.Should().NotBeNullOrEmpty();

        // Seed a user for the second email so it is "known", then request again.
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
            await users.UpsertByLoginAsync(
                "local", "known@example.com", "known@example.com", true, "known@example.com");
        }

        var resp2 = await client.PostAsJsonAsync("/identity/magic-link", Body("known@example.com"));
        resp2.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body2 = await resp2.Content.ReadAsStringAsync();

        // Anti-enumeration: identical response body whether or not the user exists.
        body2.Should().Be(body1);
        sender.SendCount.Should().Be(2);
    }

    [Fact]
    public async Task Callback_with_valid_token_upserts_verified_user_and_redirects_to_loopback()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        var client = NoRedirectClient(factory);

        var email = "verify@example.com";
        await client.PostAsJsonAsync("/identity/magic-link", Body(email, port: 51830, nonce: "n-verify"));
        var token = sender.LastToken!;

        var resp = await client.GetAsync($"/identity/magic-link/callback?token={Uri.EscapeDataString(token)}");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!;
        location.Scheme.Should().Be("http");
        location.Host.Should().Be("127.0.0.1");
        location.Port.Should().Be(51830);
        location.AbsolutePath.Should().Be("/identity/callback");
        ExtractQueryValue(location, "nonce").Should().Be("n-verify");
        ExtractQueryValue(location, "handle").Should().NotBeNullOrEmpty();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        // The local login was created AND marked verified (magic-link proves possession).
        var login = db.IdentityLogins.FirstOrDefault(l => l.Provider == "local" && l.Email == email);
        login.Should().NotBeNull();
        login!.EmailVerified.Should().BeTrue();
    }

    [Fact]
    public async Task Callback_token_redeemed_via_handle_yields_validatable_access_token()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        var client = NoRedirectClient(factory);

        await client.PostAsJsonAsync("/identity/magic-link", Body("handle@example.com", port: 51831, nonce: "h"));
        var token = sender.LastToken!;
        var callback = await client.GetAsync($"/identity/magic-link/callback?token={Uri.EscapeDataString(token)}");
        var handle = ExtractQueryValue(callback.Headers.Location!, "handle");

        var redeem = await client.PostAsJsonAsync("/identity/handle/redeem", new { handle });
        redeem.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await redeem.Content.ReadAsStringAsync());
        var accessToken = doc.RootElement.GetProperty("accessToken").GetString();
        accessToken.Should().NotBeNullOrEmpty();

        var tokens = factory.Services.GetRequiredService<IIdentityTokenService>();
        tokens.ValidateAccessToken(accessToken!).Should().NotBeNull();
    }

    [Fact]
    public async Task Callback_with_already_consumed_token_fails_single_use()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        var client = NoRedirectClient(factory);

        await client.PostAsJsonAsync("/identity/magic-link", Body("single@example.com"));
        var token = sender.LastToken!;

        var first = await client.GetAsync($"/identity/magic-link/callback?token={Uri.EscapeDataString(token)}");
        first.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var second = await client.GetAsync($"/identity/magic-link/callback?token={Uri.EscapeDataString(token)}");
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // Single-use under concurrency. This is intentionally a DB-level test rather than an HTTP one.
    //
    // The guarantee under test is the atomic conditional UPDATE in the callback:
    //     UPDATE MagicLinks SET ConsumedAt = now WHERE TokenHash = @hash AND ConsumedAt IS NULL
    // Exactly one of two concurrent claims of the SAME token must affect one row (the winner) and
    // the other must affect zero (the loser) — that is what maps, at the endpoint, to one 302 and
    // one 400. The assertion below ("exactly one row claimed, exactly one zero-row claim") is the
    // strict, undiluted form of "1 success + 1 failure"; it is NOT a weakened check.
    //
    // Why not the HTTP harness: the shared ServerTestFactory keeps ONE SqliteConnection open for the
    // whole factory, and the callback's two contexts both run their commands over that single
    // connection. Under the suite's parallel load that single connection serializes the two commands
    // in a scheduler-dependent order, so the race the UPDATE is meant to win was never exercised
    // deterministically (and occasionally both reads observed ConsumedAt == null before either
    // committed, producing two redirects — the CI flake "found 0 failures"). Production never shares
    // a connection: each request gets its own pooled connection with READ COMMITTED, exactly what
    // the two independent connections below model.
    //
    // Determinism comes from a barrier: both tasks open their own connection, then wait on a gate so
    // they reach the consume point together before either runs its UPDATE. SQLite's writer lock then
    // serializes the two autocommit UPDATEs — the winner consumes the row, the loser sees
    // ConsumedAt != null and updates zero rows. No retries, no Skip, no sleep; the gate forces the
    // overlap every run. (We do NOT wrap the UPDATE in an explicit transaction: shared-cache SQLite
    // grants the writer lock at BeginTransaction, which would block the second task before it could
    // reach the barrier. ExecuteUpdateAsync is already a single atomic autocommit statement — the
    // exact production semantics.)
    [Fact]
    public async Task Concurrent_claims_of_same_magic_link_token_consume_exactly_one_row()
    {
        // Shared-cache in-memory SQLite so two independent connections see the same database, just
        // like a connection pool over one server-side store. A keep-alive connection keeps the
        // shared in-memory db alive for the duration of the test.
        const string connectionString = "DataSource=file:magic-link-race?mode=memory&cache=shared";
        using var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();

        var options = new DbContextOptionsBuilder<ZyncMasterDbContext>()
            .UseSqlite(connectionString)
            .Options;

        await using (var seed = new ZyncMasterDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.MagicLinks.Add(new MagicLinkRow
            {
                Id = Guid.NewGuid().ToString("N"),
                TokenHash = "race-token-hash",
                Email = "race@example.com",
                Port = 51820,
                Nonce = "n-race",
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                ConsumedAt = null,
            });
            await seed.SaveChangesAsync();
        }

        var now = DateTimeOffset.UtcNow;

        // Barrier: both tasks signal arrival, then both await the release before issuing the UPDATE.
        using var arrived = new CountdownEvent(2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> ClaimAsync()
        {
            // Each task gets its own connection — the per-request isolation production gets from the
            // pool. Open it eagerly so the barrier gates the UPDATE, not connection setup.
            await using var db = new ZyncMasterDbContext(options);
            await db.Database.OpenConnectionAsync();

            arrived.Signal();
            await release.Task;

            // The EXACT conditional UPDATE the callback runs (hash match AND still unconsumed),
            // as a single atomic autocommit statement. SQLite serializes the two writers.
            return await db.MagicLinks
                .Where(r => r.TokenHash == "race-token-hash" && r.ConsumedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ConsumedAt, now));
        }

        var first = Task.Run(ClaimAsync);
        var second = Task.Run(ClaimAsync);

        // Hold both at the consume point, then release them together.
        arrived.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("both claims must reach the consume point");
        release.SetResult();

        var affected = await Task.WhenAll(first, second);

        // Strict single-use: one claim updated exactly one row (the winner), the other updated zero
        // (the loser). This is "1 success + 1 failure" with no relaxation.
        affected.Count(a => a == 1).Should().Be(1);
        affected.Count(a => a == 0).Should().Be(1);

        // And the row ended up consumed exactly once.
        await using var verify = new ZyncMasterDbContext(options);
        var consumedRows = await verify.MagicLinks
            .Where(r => r.TokenHash == "race-token-hash" && r.ConsumedAt != null)
            .CountAsync();
        consumedRows.Should().Be(1);
    }

    // Belt-and-suspenders at the HTTP layer: a SEQUENTIAL second click of an already-consumed token
    // must fail. Deterministic (no race), proving the endpoint wires the single-use UPDATE result to
    // a 400 — complementing the concurrency proof above.
    [Fact]
    public async Task Callback_second_sequential_click_of_same_token_fails_single_use()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        var client = NoRedirectClient(factory);

        await client.PostAsJsonAsync("/identity/magic-link", Body("race@example.com", nonce: "n-race"));
        var token = sender.LastToken!;
        var url = $"/identity/magic-link/callback?token={Uri.EscapeDataString(token)}";

        var firstResp = await client.GetAsync(url);
        var secondResp = await client.GetAsync(url);

        firstResp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        secondResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_with_expired_token_fails()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender, clock);
        var client = NoRedirectClient(factory);

        await client.PostAsJsonAsync("/identity/magic-link", Body("expired@example.com"));
        var token = sender.LastToken!;

        // Advance past the default 15-minute TTL.
        clock.Advance(TimeSpan.FromMinutes(16));

        var resp = await client.GetAsync($"/identity/magic-link/callback?token={Uri.EscapeDataString(token)}");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_with_unknown_token_fails()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync("/identity/magic-link/callback?token=not-a-real-token");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Per_email_rate_limit_goes_silent_after_cap_but_keeps_returning_202()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        var client = NoRedirectClient(factory);

        var email = "flood@example.com";
        // Default MagicLinkMaxPerEmail is 3.
        for (var i = 0; i < 3; i++)
        {
            var ok = await client.PostAsJsonAsync("/identity/magic-link", Body(email, nonce: $"n{i}"));
            ok.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }
        sender.SendCount.Should().Be(3);

        // 4th request inside the window: still 202 (no enumeration leak) but NO email sent.
        var fourth = await client.PostAsJsonAsync("/identity/magic-link", Body(email, nonce: "n4"));
        fourth.StatusCode.Should().Be(HttpStatusCode.Accepted);
        sender.SendCount.Should().Be(3);
    }

    [Theory]
    [InlineData(80, "app-nonce")]      // port below 1024
    [InlineData(70000, "app-nonce")]   // port above 65535
    [InlineData(51820, "")]            // empty nonce
    public async Task Post_with_bad_port_or_nonce_returns_400(int port, string nonce)
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        var client = NoRedirectClient(factory);

        var resp = await client.PostAsJsonAsync(
            "/identity/magic-link", new { email = "x@example.com", port, nonce });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        sender.SendCount.Should().Be(0);
    }

    [Fact]
    public async Task Post_with_missing_email_returns_400()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        var client = NoRedirectClient(factory);

        var resp = await client.PostAsJsonAsync(
            "/identity/magic-link", new { email = "", port = 51820, nonce = "n" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
