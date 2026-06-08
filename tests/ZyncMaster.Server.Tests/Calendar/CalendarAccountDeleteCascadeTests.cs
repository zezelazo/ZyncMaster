using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ZyncMaster.Server.Tests.Calendar;

// Track A-3 — DELETE /api/calendar/accounts/{id} must DELETE every sync pair that references the
// deleted account (source or destination) and leave pairs for other accounts/users untouched.
// Exercised through the real bearer-gated endpoint over the SQLite harness.
public sealed class CalendarAccountDeleteCascadeTests
{
    private static WebApplicationFactory<Program> Build() => new ServerTestFactory();

    private static HttpClient NoRedirectClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    // Mints a real identity bearer for a fresh user and returns (bearer, userId).
    private static (string token, string userId) IssueBearer(
        WebApplicationFactory<Program> factory, string subject, string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var tokens = scope.ServiceProvider.GetRequiredService<IIdentityTokenService>();
        var user = users.UpsertByLoginAsync(
            "local", subject, email, emailVerified: true, displayName: subject, CancellationToken.None)
            .GetAwaiter().GetResult();
        return (tokens.IssueAccessToken(user).Token, user.Id);
    }

    private static HttpRequestMessage Bearer(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    // Seeds a pool calendar account for the given user via the user-scoped store. The bearer
    // request later runs under the same userId, so the store resolves it.
    private static async Task<string> SeedAccountAsync(
        WebApplicationFactory<Program> factory, string userId, string email)
    {
        var id = Guid.NewGuid().ToString("N");
        var accounts = factory.Services.GetRequiredService<ICalendarAccountStore>();
        // The EF store reads ICurrentUserAccessor for the userId; there is no HTTP context here,
        // so it would write under "default". Insert the row directly with the right UserId.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMaster.Server.Data.ZyncMasterDbContext>();
        db.CalendarAccounts.Add(new ZyncMaster.Server.Data.CalendarAccountRow
        {
            Id = id,
            UserId = userId,
            Kind = AccountKind.Graph.ToString(),
            Provider = "microsoft",
            AccountEmail = email,
            Scope = AccountScope.ReadWrite.ToString(),
            Status = "active",
            ConnectedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task SeedPairAsync(
        WebApplicationFactory<Program> factory, string userId, string id, string srcRef, string dstRef)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMaster.Server.Data.ZyncMasterDbContext>();
        var source = new Endpoint { Provider = "MicrosoftGraph", AccountRef = srcRef, CalendarId = "s" };
        var dest = new Endpoint { Provider = "MicrosoftGraph", AccountRef = dstRef, CalendarId = "d" };
        db.SyncPairs.Add(new ZyncMaster.Server.Data.SyncPairRow
        {
            Id = id,
            UserId = userId,
            Name = id,
            SourceJson = System.Text.Json.JsonSerializer.Serialize(new { provider = source.Provider, accountRef = source.AccountRef, calendarId = source.CalendarId }),
            DestinationJson = System.Text.Json.JsonSerializer.Serialize(new { provider = dest.Provider, accountRef = dest.AccountRef, calendarId = dest.CalendarId }),
            IntervalMin = 10,
            State = "active",
        });
        await db.SaveChangesAsync();
    }

    private static async Task<string?> PairStateAsync(WebApplicationFactory<Program> factory, string pairId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMaster.Server.Data.ZyncMasterDbContext>();
        var row = await db.SyncPairs.FindAsync(pairId);
        return row?.State;
    }

    [Fact]
    public async Task Delete_account_deletes_pairs_referencing_it_on_either_side()
    {
        var factory = Build();
        var (token, userId) = IssueBearer(factory, "del-cascade", "del-cascade@test");
        var accountId = await SeedAccountAsync(factory, userId, "victim@test");
        var otherAccountId = await SeedAccountAsync(factory, userId, "other@test");

        await SeedPairAsync(factory, userId, "p-dest", srcRef: otherAccountId, dstRef: accountId);
        await SeedPairAsync(factory, userId, "p-src", srcRef: accountId, dstRef: otherAccountId);
        await SeedPairAsync(factory, userId, "p-unrelated", srcRef: otherAccountId, dstRef: otherAccountId);

        var client = NoRedirectClient(factory);
        var resp = await client.SendAsync(Bearer(HttpMethod.Delete, $"/api/calendar/accounts/{accountId}", token));

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Forget DELETES the referencing pairs: their rows are gone, so PairStateAsync (row?.State)
        // is null. The unrelated pair is left active.
        (await PairStateAsync(factory, "p-dest")).Should().BeNull();
        (await PairStateAsync(factory, "p-src")).Should().BeNull();
        (await PairStateAsync(factory, "p-unrelated")).Should().Be("active");
    }

    [Fact]
    public async Task Delete_account_does_not_touch_another_users_pairs()
    {
        var factory = Build();
        var (tokenA, userIdA) = IssueBearer(factory, "owner-a", "owner-a@test");
        var (_, userIdB) = IssueBearer(factory, "owner-b", "owner-b@test");

        var accountA = await SeedAccountAsync(factory, userIdA, "a@test");
        var accountB = await SeedAccountAsync(factory, userIdB, "b@test");
        await SeedPairAsync(factory, userIdB, "p-b", srcRef: accountB, dstRef: accountB);

        var client = NoRedirectClient(factory);
        var resp = await client.SendAsync(Bearer(HttpMethod.Delete, $"/api/calendar/accounts/{accountA}", tokenA));

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // User B's pair is untouched.
        (await PairStateAsync(factory, "p-b")).Should().Be("active");
    }

    [Fact]
    public async Task Delete_unknown_account_returns_404()
    {
        var factory = Build();
        var (token, _) = IssueBearer(factory, "unknown-del", "unknown-del@test");
        var client = NoRedirectClient(factory);

        var resp = await client.SendAsync(Bearer(HttpMethod.Delete, "/api/calendar/accounts/does-not-exist", token));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
