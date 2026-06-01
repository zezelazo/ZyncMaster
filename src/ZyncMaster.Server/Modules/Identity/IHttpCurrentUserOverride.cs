using Microsoft.AspNetCore.Http;

namespace ZyncMaster.Server;

// Sets/clears the per-request current-user override that HttpContextCurrentUserAccessor reads
// first (HttpContext.Items[OverrideItemKey]). It is the seam the cron runner uses to execute a
// due pair UNDER ITS OWNER's identity within the single /api/sync/run-due request, so the
// downstream user-scoped Graph token resolution picks the owner's connected account. The OAuth
// callback uses the same Items key directly; this interface exposes it for the cron path and keeps
// the cron runner testable without an HttpContext.
//
// Singleton-safe like the accessor: it holds only IHttpContextAccessor and reads HttpContext per
// call, so the override applies to whatever request is currently executing on this thread.
public interface IHttpCurrentUserOverride
{
    void Set(string userId);
    void Clear();
}

public sealed class HttpCurrentUserOverride : IHttpCurrentUserOverride
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpCurrentUserOverride(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    }

    public void Set(string userId)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is not null)
            context.Items[HttpContextCurrentUserAccessor.OverrideItemKey] = userId;
    }

    public void Clear()
    {
        var context = _httpContextAccessor.HttpContext;
        context?.Items.Remove(HttpContextCurrentUserAccessor.OverrideItemKey);
    }
}
