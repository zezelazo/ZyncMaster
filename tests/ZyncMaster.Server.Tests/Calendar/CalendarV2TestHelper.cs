using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZyncMaster.Graph;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server.Tests.Calendar;

// Shared plumbing for the calendar v2 endpoint tests: a recording fake of the per-account
// replica client/responder factories, identity-bearer minting (same pattern as
// CalendarConnectEndpointsTests.IssueBearer) and direct row seeding of calendar accounts.
public static class CalendarV2TestHelper
{
    // In-memory IReplicaGraphClient. Configure per-account behavior via the public fields.
    public sealed class FakeReplicaClient : IReplicaGraphClient
    {
        public ConcurrentDictionary<string, SourceEventSnapshot> EventsById { get; } = new();
        public List<(string CalendarId, ReplicaDraft Draft)> CreatedReplicas { get; } = new();
        public List<(string CalendarId, OriginEventDraft Draft)> CreatedOrigins { get; } = new();
        public List<(string EventId, ReplicaDraft Draft)> PatchedTimes { get; } = new();
        public List<(string EventId, string Subject)> PatchedSubjects { get; } = new();
        public List<(string EventId, string RuleId)> Stamps { get; } = new();
        public List<string> Deleted { get; } = new();
        public List<CalendarTargetInfo> Calendars { get; } = new();
        public List<SourceEventSnapshot> WindowEvents { get; } = new();
        public List<ReplicaEventRef> WindowReplicas { get; } = new();
        public int NextEventId;

        public Task<IReadOnlyList<CalendarTargetInfo>> ListCalendarsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CalendarTargetInfo>>(Calendars);

        public Task<SourceEventSnapshot?> GetEventAsync(string eventId, CancellationToken ct = default)
            => Task.FromResult(EventsById.TryGetValue(eventId, out var s) ? s : null);

        public Task<IReadOnlyList<SourceEventSnapshot>> ListWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SourceEventSnapshot>>(WindowEvents);

        public Task<string> CreateReplicaAsync(string calendarId, ReplicaDraft draft, CancellationToken ct = default)
        {
            CreatedReplicas.Add((calendarId, draft));
            return Task.FromResult($"created-{Interlocked.Increment(ref NextEventId)}");
        }

        public Task UpdateReplicaTimesAsync(string eventId, ReplicaDraft draft, CancellationToken ct = default)
        {
            PatchedTimes.Add((eventId, draft));
            return Task.CompletedTask;
        }

        public Task UpdateSubjectAsync(string eventId, string subject, CancellationToken ct = default)
        {
            PatchedSubjects.Add((eventId, subject));
            return Task.CompletedTask;
        }

        public Task StampRuleProcessedAsync(string eventId, string ruleId, CancellationToken ct = default)
        {
            Stamps.Add((eventId, ruleId));
            return Task.CompletedTask;
        }

        public Task DeleteEventAsync(string eventId, CancellationToken ct = default)
        {
            Deleted.Add(eventId);
            return Task.CompletedTask;
        }

        public Task<string> CreateOriginEventAsync(string calendarId, OriginEventDraft draft, CancellationToken ct = default)
        {
            CreatedOrigins.Add((calendarId, draft));
            return Task.FromResult($"origin-{Interlocked.Increment(ref NextEventId)}");
        }

        public Task<IReadOnlyList<ReplicaEventRef>> ListReplicasInWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReplicaEventRef>>(WindowReplicas);
    }

    public sealed class FakeResponder : IEventResponder
    {
        public List<(string EventId, RespondAction Action, string? Comment)> Responses { get; } = new();
        public List<(string EventId, string? Comment)> Cancels { get; } = new();

        public Task RespondAsync(string eventId, RespondAction action, string? comment, CancellationToken ct = default)
        {
            Responses.Add((eventId, action, comment));
            return Task.CompletedTask;
        }

        public Task CancelMeetingAsync(string eventId, string? comment, CancellationToken ct = default)
        {
            Cancels.Add((eventId, comment));
            return Task.CompletedTask;
        }
    }

    // Test host with the Graph factories replaced by per-account fakes. clients[accountId]
    // must contain every account the test touches; a missing key throws loudly.
    public static WebApplicationFactory<Program> CreateFactory(
        IDictionary<string, FakeReplicaClient> clients,
        IDictionary<string, FakeResponder>? responders = null) =>
        new ServerTestFactory().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<Func<string, IReplicaGraphClient>>();
            s.AddSingleton<Func<string, IReplicaGraphClient>>(_ =>
                accountId => clients[accountId]);
            s.RemoveAll<Func<string, IEventResponder>>();
            s.AddSingleton<Func<string, IEventResponder>>(_ =>
                accountId => responders is null ? new FakeResponder() : responders[accountId]);
        }));

    // Mints a real identity bearer for a fresh user and returns (bearer, userId).
    public static (string Token, string UserId) IssueBearer(
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

    // Inserts a calendar account row directly (the OAuth connect flow is not under test here).
    public static void SeedAccount(
        WebApplicationFactory<Program> factory, string userId, string accountId,
        AccountKind kind = AccountKind.Graph, AccountScope scope = AccountScope.ReadWrite)
    {
        using var scope_ = factory.Services.CreateScope();
        var db = scope_.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        db.CalendarAccounts.Add(new CalendarAccountRow
        {
            Id = accountId,
            UserId = userId,
            Kind = kind.ToString(),
            Provider = kind == AccountKind.Graph ? "microsoft" : "outlook-com",
            AccountEmail = $"{accountId}@test",
            Scope = scope.ToString(),
            Status = "active",
            ConnectedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    public static HttpRequestMessage Bearer(HttpMethod method, string url, string? token, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (token is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            req.Content = System.Net.Http.Json.JsonContent.Create(body);
        return req;
    }
}
