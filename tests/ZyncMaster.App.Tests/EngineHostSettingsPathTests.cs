using System;
using System.IO;
using FluentAssertions;
using ZyncMaster.App.Configuration;
using ZyncMaster.Core;
using Xunit;

namespace ZyncMaster.App.Tests;

// FIX 1 — settings.json moved from "next to the exe" (which crashed the app on first launch when the
// exe lived in a read-only location such as Program Files or a still-mounted zip) to the
// user-writable %LOCALAPPDATA%\ZyncMaster\App\ tree. These tests pin the new location and prove the
// failure mode the move + try/catch defends against.
public class EngineHostSettingsPathTests
{
    [Fact]
    public void DefaultSettingsPath_is_under_localappdata_zyncmaster_app()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZyncMaster", "App", "settings.json");

        EngineHost.DefaultSettingsPath().Should().Be(expected);
    }

    [Fact]
    public void DefaultSettingsPath_shares_the_tree_with_device_key()
    {
        // device.key / identity.token / the WebView2 user data all live under
        // %LOCALAPPDATA%\ZyncMaster\App\. settings.json must sit in the SAME user-writable tree so a
        // read-only install dir can never block writing it.
        var appTree = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZyncMaster", "App");

        Path.GetDirectoryName(EngineHost.DefaultSettingsPath()).Should().Be(appTree);
    }

    [Fact]
    public void Settings_round_trip_in_a_user_writable_directory()
    {
        // Mirrors what LoadOrCreateDefault does at the new path: generate defaults on first run,
        // then read them back. Uses a temp dir to stand in for the (writable) LOCALAPPDATA tree.
        var dir = Path.Combine(Path.GetTempPath(), "zm_app_settings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var repo = new SettingsRepository<AppSettings>(new PhysicalFileSystem());

            var created = repo.LoadOrCreateDefault(path);
            File.Exists(path).Should().BeTrue("first run must create settings.json in the writable dir");
            created.ServerBaseUrl.Should().Be(AppSettings.ProductionServerBaseUrl);

            var reloaded = repo.LoadOrCreateDefault(path);
            reloaded.ServerBaseUrl.Should().Be(AppSettings.ProductionServerBaseUrl);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void LoadOrCreateDefault_into_an_unwritable_location_throws_an_IOException()
    {
        // This is the exact failure the FIX defends against: when settings.json cannot be created
        // (here, because its parent directory does not exist and cannot be implicitly created by
        // File.WriteAllText), the repository throws an IOException-derived exception. The old
        // EngineHost let this propagate out of EngineHost.Create and App.TryWireEngine only caught
        // SettingsValidation/Load — so the app CRASHED on first launch. App now ALSO catches
        // IOException / UnauthorizedAccessException and degrades to the unconfigured actions.
        var unwritable = Path.Combine(
            Path.GetTempPath(),
            "zm_missing_" + Guid.NewGuid().ToString("N"),
            "no", "such", "dir", "settings.json");

        var repo = new SettingsRepository<AppSettings>(new PhysicalFileSystem());

        var act = () => repo.LoadOrCreateDefault(unwritable);

        act.Should().Throw<IOException>();
    }
}
