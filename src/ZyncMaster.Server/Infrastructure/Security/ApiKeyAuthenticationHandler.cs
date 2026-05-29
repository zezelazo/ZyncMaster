using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ZyncMaster.Server;

public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    private readonly IDeviceStore _store;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IDeviceStore store)
        : base(options, logger, encoder)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values))
            return AuthenticateResult.NoResult();

        var key = values.ToString();
        if (string.IsNullOrEmpty(key))
            return AuthenticateResult.NoResult();

        var devices = await _store.ListAsync(Context.RequestAborted);
        var device = devices.FirstOrDefault(d => ApiKeyHasher.Verify(key, d.ApiKeyHash));
        if (device is null)
            return AuthenticateResult.Fail("Invalid API key");

        var updated = device with { LastSeenUtc = DateTimeOffset.UtcNow };
        await _store.UpdateAsync(updated, Context.RequestAborted);

        var identity = new ClaimsIdentity(
            new[] { new Claim("deviceId", device.Id) },
            SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
