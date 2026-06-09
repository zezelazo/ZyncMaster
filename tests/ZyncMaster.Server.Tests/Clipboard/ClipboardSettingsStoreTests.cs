using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Server.Tests.Clipboard;

public class ClipboardSettingsStoreTests
{
    [Fact]
    public async Task Get_unknown_returns_defaults()
    {
        var store = ClipboardTestHarness.SettingsStore("u1");
        var s = await store.GetAsync("nope");
        s.DeviceId.Should().Be("nope");
        s.AutoSync.Should().BeTrue();
        s.Send.Should().BeTrue();
        s.Receive.Should().BeTrue();
        s.Density.Should().Be("rich");
        s.ViewerHotkey.Should().Be("Ctrl+Win+Q");
        s.ShowHints.Should().BeTrue();
    }

    [Fact]
    public async Task Upsert_then_Get_roundtrips()
    {
        var store = ClipboardTestHarness.SettingsStore("u1");
        await store.UpsertAsync(new ClipboardDeviceSettings { DeviceId = "d1", Send = false, Density = "mini" });
        var s = await store.GetAsync("d1");
        s.DeviceId.Should().Be("d1");
        s.Send.Should().BeFalse();
        s.Density.Should().Be("mini");
        // Untouched fields keep their record defaults.
        s.AutoSync.Should().BeTrue();
        s.Receive.Should().BeTrue();
    }

    [Fact]
    public async Task Upsert_then_Upsert_updates_not_duplicates()
    {
        var store = ClipboardTestHarness.SettingsStore("u1");
        await store.UpsertAsync(new ClipboardDeviceSettings { DeviceId = "d1", Density = "rich" });
        await store.UpsertAsync(new ClipboardDeviceSettings { DeviceId = "d1", Density = "mini", AutoSync = false });

        var all = await store.ListAsync();
        all.Should().HaveCount(1);
        var s = await store.GetAsync("d1");
        s.Density.Should().Be("mini");
        s.AutoSync.Should().BeFalse();
    }

    [Fact]
    public async Task Upsert_roundtrips_key_admission_fields_on_insert_and_update()
    {
        var store = ClipboardTestHarness.SettingsStore("u1");

        // INSERT path: a device advertises its public key and that it is waiting for the text key.
        await store.UpsertAsync(new ClipboardDeviceSettings
        {
            DeviceId = "d1",
            PublicKeyBase64 = "cHViLWtleQ==",
            NeedsTextKey = true,
        });
        var s = await store.GetAsync("d1");
        s.PublicKeyBase64.Should().Be("cHViLWtleQ==");
        s.NeedsTextKey.Should().BeTrue();

        // UPDATE path: admission completed — the flag drops, the key stays published.
        await store.UpsertAsync(new ClipboardDeviceSettings
        {
            DeviceId = "d1",
            PublicKeyBase64 = "cHViLWtleQ==",
            NeedsTextKey = false,
        });
        var after = await store.GetAsync("d1");
        after.NeedsTextKey.Should().BeFalse();
        after.PublicKeyBase64.Should().Be("cHViLWtleQ==");

        // Defaults for a never-stored device: no key, not waiting.
        var unknown = await store.GetAsync("other");
        unknown.PublicKeyBase64.Should().BeNull();
        unknown.NeedsTextKey.Should().BeFalse();
    }

    [Fact]
    public async Task List_is_user_scoped()
    {
        var s1 = ClipboardTestHarness.SettingsStore("su1", shareDb: true);
        var s2 = ClipboardTestHarness.SettingsStore("su2", shareDb: true); // same DB, different user
        await s1.UpsertAsync(new ClipboardDeviceSettings { DeviceId = "su1-dev" });

        (await s2.ListAsync()).Should().BeEmpty();
        (await s1.ListAsync()).Select(x => x.DeviceId).Should().Equal("su1-dev");
    }
}
