#if WIN_WEBVIEW2
using FluentAssertions;
using ZyncMaster.App.Windows;
using Xunit;

namespace ZyncMaster.App.Tests;

// FIX 2 — when the Evergreen WebView2 Runtime is missing the embedded host cannot initialise and the
// frameless window used to stay blank with no explanation. The host now exposes a runtime download
// URL and an InitializationFailed signal so the window can swap in a native "WebView2 Runtime no
// instalado" panel with a link to the download page. These tests pin the surface the window relies
// on (the full init failure path needs a live WebView2 environment + Avalonia dispatcher, so it is
// not unit-testable here — it is exercised at runtime).
public class WebView2WebHostFallbackTests
{
    [Fact]
    public void RuntimeDownloadUrl_points_at_the_microsoft_webview2_page()
    {
        WebView2WebHost.RuntimeDownloadUrl
            .Should().Be("https://developer.microsoft.com/microsoft-edge/webview2/");
    }

    [Fact]
    public void RuntimeDownloadUrl_is_an_https_web_url()
    {
        // The window only shell-opens http/https URLs; the download link must qualify.
        WebView2WebHost.RuntimeDownloadUrl.Should().StartWith("https://");
    }
}
#endif
