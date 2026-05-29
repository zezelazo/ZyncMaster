using Microsoft.AspNetCore.Http;

namespace ZyncMaster.Server;

// Resolves the identity of the caller for the current operation.
//
// IMPORTANT — singleton-safety contract: the EF-backed stores are registered as
// SINGLETONS (over IDbContextFactory). Any ICurrentUserAccessor injected into them is
// therefore captured for the lifetime of the process, so the accessor MUST be singleton
// itself and read the ambient identity PER CALL. The production implementation
// (HttpContextCurrentUserAccessor) does exactly this: it holds only IHttpContextAccessor
// (also a singleton) and re-reads HttpContext.User on every UserId access. A scoped
// accessor here would be a captive dependency and every request would see the first
// request's user. Do not change the lifetime.
public interface ICurrentUserAccessor
{
    string UserId { get; }
}

// Fixed single-user stub. Returns the seeded "default" user id. Still used by the
// store-double unit tests and any composition that has no HTTP context.
public sealed class DefaultCurrentUserAccessor : ICurrentUserAccessor
{
    public const string DefaultUserId = "default";

    public string UserId => DefaultUserId;
}

// Singleton-safe production accessor. Reads the ambient HttpContext per call (never
// caches it) so it can be injected into the singleton stores without becoming a captive
// dependency. Resolution order per call:
//   1. A per-request override placed in HttpContext.Items[OverrideKey] (used by the
//      OAuth callback, which must persist data for the just-created user before that
//      user's auth cookie is active within the same request).
//   2. The "userId" claim on HttpContext.User (set by both the Cookie and ApiKey schemes).
//   3. The seeded "default" user, so non-authenticated / internal paths keep working.
public sealed class HttpContextCurrentUserAccessor : ICurrentUserAccessor
{
    public const string OverrideItemKey = "ZyncMaster.CurrentUserOverride";
    public const string UserIdClaimType = "userId";

    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCurrentUserAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor
            ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    }

    public string UserId
    {
        get
        {
            var context = _httpContextAccessor.HttpContext;
            if (context is null)
                return DefaultCurrentUserAccessor.DefaultUserId;

            if (context.Items.TryGetValue(OverrideItemKey, out var raw) &&
                raw is string overrideId &&
                !string.IsNullOrEmpty(overrideId))
            {
                return overrideId;
            }

            var claim = context.User?.FindFirst(UserIdClaimType)?.Value;
            return string.IsNullOrEmpty(claim)
                ? DefaultCurrentUserAccessor.DefaultUserId
                : claim;
        }
    }
}
