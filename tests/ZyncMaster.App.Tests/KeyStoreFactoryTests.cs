using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.App.Platform;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.App.Tests;

public class KeyStoreFactoryTests
{
    private sealed class FakePlatform : IPlatform
    {
        public bool IsWindows { get; init; }
        public bool IsMacOS { get; init; }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "zyncmaster_keystore_" + Guid.NewGuid().ToString("N"), "device.key");

    [Fact]
    public void Windows_branch_returns_dpapi_store()
    {
        var store = KeyStoreFactory.Create(new FakePlatform { IsWindows = true }, TempPath(), new FixedClock());

        store.Should().BeOfType<DpapiDeviceKeyStore>();
    }

    [Fact]
    public void Mac_branch_returns_keychain_store()
    {
        var store = KeyStoreFactory.Create(new FakePlatform { IsMacOS = true }, TempPath(), new FixedClock());

        store.Should().BeOfType<KeychainDeviceKeyStore>();
    }

    [Fact]
    public void Other_branch_returns_file_fallback_store()
    {
        var store = KeyStoreFactory.Create(new FakePlatform(), TempPath(), new FixedClock());

        store.Should().BeOfType<FileDeviceKeyStore>();
    }

    [Fact]
    public void Null_arguments_throw()
    {
        var clock = new FixedClock();

        ((Action)(() => KeyStoreFactory.Create(null!, TempPath(), clock))).Should().Throw<ArgumentNullException>();
        ((Action)(() => KeyStoreFactory.Create(new FakePlatform(), null!, clock))).Should().Throw<ArgumentNullException>();
        ((Action)(() => KeyStoreFactory.Create(new FakePlatform(), TempPath(), null!))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task File_fallback_store_round_trips()
    {
        var path = TempPath();
        var store = KeyStoreFactory.Create(new FakePlatform(), path, new FixedClock());

        await store.SaveAsync("secret-key-123", CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);
        loaded.Should().Be("secret-key-123");

        await store.ClearAsync(CancellationToken.None);
        var afterClear = await store.LoadAsync(CancellationToken.None);
        afterClear.Should().BeNull();
    }

    [Fact]
    public async Task Keychain_round_trip_on_mac_only()
    {
        if (!OperatingSystem.IsMacOS())
            return; // Keychain CLI is only available on macOS.

        var store = new KeychainDeviceKeyStore("ZyncMasterTest", "device-api-key-test");
        try
        {
            await store.SaveAsync("mac-secret-456", CancellationToken.None);
            var loaded = await store.LoadAsync(CancellationToken.None);
            loaded.Should().Be("mac-secret-456");
        }
        finally
        {
            await store.ClearAsync(CancellationToken.None);
        }
    }
}
