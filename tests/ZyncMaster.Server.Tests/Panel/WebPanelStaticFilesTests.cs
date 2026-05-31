using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Server.Tests.Panel;

// The Server serves two static surfaces: the marketing LANDING (repo-root web/) at "/" and the
// canonical Liquid Glass dashboard UI (repo-root ui/) under "/app". These tests prove the
// landing is served at "/", the dashboard and its CSS/JS assets resolve under "/app", and that
// the static-file middleware does not shadow the JSON API or the OAuth/health routes.
public class WebPanelStaticFilesTests : IClassFixture<ServerTestFactory>
{
    private readonly ServerTestFactory _factory;

    public WebPanelStaticFilesTests(ServerTestFactory factory) => _factory = factory;

    [Fact]
    public async Task Root_serves_the_marketing_landing()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Zync Master");
        // Markers unique to the launcher (not the dashboard). The launcher has no sign-in:
        // identity/sign-in lives in the desktop app, the web is a download/marketing surface.
        body.Should().Contain("css/launcher.css");
        body.Should().Contain("Download for Windows");
    }

    [Fact]
    public async Task App_serves_the_real_liquid_glass_index()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/app/");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Zync Master");
        // Markers unique to the real UI (the removed placeholder had none of these).
        body.Should().Contain("js/app.js");
        body.Should().Contain("css/tokens.css");
    }

    [Fact]
    public async Task App_bare_path_redirects_to_trailing_slash()
    {
        var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

        var resp = await client.GetAsync("/app");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Be("/app/");
    }

    [Fact]
    public async Task App_js_asset_is_served_as_javascript()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/app/js/app.js");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType
            .Should().BeOneOf("text/javascript", "application/javascript");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Zync Master UI");
    }

    [Fact]
    public async Task Css_asset_is_served_as_css()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/app/css/tokens.css");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/css");
    }

    [Fact]
    public async Task Static_files_do_not_shadow_the_json_api()
    {
        var client = _factory.CreateClient();

        // /api/panel/status is a cookie-gated JSON endpoint; with no cookie it must still be
        // reached by routing (401), never intercepted/404'd by the static-file middleware.
        var resp = await client.GetAsync("/api/panel/status");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Static_files_do_not_shadow_health()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("ok");
    }

    [Fact]
    public async Task Connect_honors_returnTo_root_for_the_panel_signin()
    {
        // The panel's "Sign in with Microsoft" button navigates to /connect?returnTo=/. The
        // endpoint must accept it and redirect to the Microsoft authorize URL (no auth needed).
        var client = _factory.WithWebHostBuilder(_ => { }).CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

        var resp = await client.GetAsync("/connect?returnTo=/");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Contain("authorize");
    }
}
