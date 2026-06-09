using System;
using System.IO;
using FluentAssertions;
using Newtonsoft.Json;
using ZyncMaster.App.Configuration;
using ZyncMaster.Core;
using Xunit;

namespace ZyncMaster.App.Tests;

// Auto-heal for installs whose persisted serverBaseUrl is the retired Azure host. Before the VPS
// cutover the app wrote https://zyncmaster.azurewebsites.net into settings.json; that host no longer
// answers, so on load EngineHost rewrites it to the production URL and persists the fix so it sticks.
public class EngineHostServerUrlMigrationTests
{
    [Theory]
    [InlineData("https://zyncmaster.azurewebsites.net")]
    [InlineData("https://zyncmaster.azurewebsites.net/")]
    [InlineData("  https://zyncmaster.azurewebsites.net  ")]
    [InlineData("https://ZyncMaster.AzureWebsites.net")]
    public void MigrateRetiredServerUrl_rewrites_the_azure_host_to_production(string persisted)
    {
        var settings = new AppSettings { ServerBaseUrl = persisted };

        var changed = EngineHost.MigrateRetiredServerUrl(settings);

        changed.Should().BeTrue();
        settings.ServerBaseUrl.Should().Be(AppSettings.ProductionServerBaseUrl);
    }

    [Theory]
    [InlineData("https://api.devlabperu.com/zync")]
    [InlineData("http://localhost:5007")]
    [InlineData("https://staging.example.com/zync")]
    public void MigrateRetiredServerUrl_leaves_a_non_azure_url_untouched(string custom)
    {
        var settings = new AppSettings { ServerBaseUrl = custom };

        var changed = EngineHost.MigrateRetiredServerUrl(settings);

        changed.Should().BeFalse();
        settings.ServerBaseUrl.Should().Be(custom);
    }

    [Fact]
    public void MigrateRetiredServerUrl_is_null_safe()
    {
        var settings = new AppSettings { ServerBaseUrl = null };

        var changed = EngineHost.MigrateRetiredServerUrl(settings);

        changed.Should().BeFalse();
        settings.ServerBaseUrl.Should().BeNull();
    }

    [Fact]
    public void A_settings_file_with_the_azure_url_is_migrated_and_persisted_on_load()
    {
        // End-to-end mirror of EngineHost.Create's load path: a settings.json carrying the retired
        // Azure host is loaded, migrated, and the rewrite is saved back so a later load reads prod.
        var dir = Path.Combine(Path.GetTempPath(), "zm_migrate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{\"serverBaseUrl\":\"https://zyncmaster.azurewebsites.net\"}");

            var repo = new SettingsRepository<AppSettings>(new PhysicalFileSystem());

            var loaded = repo.LoadOrCreateDefault(path);
            if (EngineHost.MigrateRetiredServerUrl(loaded))
                repo.Save(loaded, path);

            loaded.ServerBaseUrl.Should().Be(AppSettings.ProductionServerBaseUrl);

            // The fix must be durable: a fresh load reads the production URL, not the Azure host.
            var reloaded = repo.LoadOrCreateDefault(path);
            reloaded.ServerBaseUrl.Should().Be(AppSettings.ProductionServerBaseUrl);

            var raw = File.ReadAllText(path);
            raw.Should().NotContain("azurewebsites.net");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void A_settings_file_with_a_custom_url_is_left_untouched_on_load()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zm_migrate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string custom = "https://my-private-server.example/zync";
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(new AppSettings { ServerBaseUrl = custom }));

            var repo = new SettingsRepository<AppSettings>(new PhysicalFileSystem());

            var loaded = repo.LoadOrCreateDefault(path);
            var changed = EngineHost.MigrateRetiredServerUrl(loaded);

            changed.Should().BeFalse();
            loaded.ServerBaseUrl.Should().Be(custom);

            var reloaded = repo.LoadOrCreateDefault(path);
            reloaded.ServerBaseUrl.Should().Be(custom);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
