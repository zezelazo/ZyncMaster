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
    public void LoadOrCreateDefault_creates_missing_parent_directories()
    {
        // Regression (real bug): on a fresh machine the settings dir (%LOCALAPPDATA%\ZyncMaster\App)
        // may not exist yet when EngineHost.Create runs — WebView2 creates it only later, when it
        // mounts. Previously SettingsRepository.Save did a bare File.WriteAllText, which threw
        // DirectoryNotFoundException; EngineHost.Create propagated it and the App silently degraded to
        // the "unconfigured" gate ("Set the server URL"), with NO settings.json on disk. Save now
        // creates the parent directory chain first, so generating the default succeeds and the app is
        // configured against the production server on first launch.
        var root = Path.Combine(Path.GetTempPath(), "zm_missing_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "no", "such", "dir", "settings.json");
        var repo = new SettingsRepository<AppSettings>(new PhysicalFileSystem());
        try
        {
            var created = repo.LoadOrCreateDefault(path);

            File.Exists(path).Should().BeTrue("Save must create the missing parent directories then write");
            created.ServerBaseUrl.Should().Be(AppSettings.ProductionServerBaseUrl);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
