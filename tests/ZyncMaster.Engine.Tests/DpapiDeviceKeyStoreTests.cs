using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests;

public sealed class DpapiDeviceKeyStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _storePath;

    public DpapiDeviceKeyStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "smkeystore_" + Guid.NewGuid().ToString("N"));
        _storePath = Path.Combine(_dir, "nested", "device.key");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch { /* best effort */ }
    }

    private DpapiDeviceKeyStore BuildSut() => new DpapiDeviceKeyStore(_storePath, new SystemClock());

    [Fact]
    public async Task SaveThenLoad_RoundTrips()
    {
        var sut = BuildSut();
        await sut.SaveAsync("super-secret-api-key", CancellationToken.None);

        var loaded = await sut.LoadAsync(CancellationToken.None);

        loaded.Should().Be("super-secret-api-key");
    }

    [Fact]
    public async Task Save_CreatesParentDirectory()
    {
        var sut = BuildSut();
        await sut.SaveAsync("k", CancellationToken.None);

        File.Exists(_storePath).Should().BeTrue();
    }

    [Fact]
    public async Task Load_NothingStored_ReturnsNull()
    {
        var loaded = await BuildSut().LoadAsync(CancellationToken.None);
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task Clear_RemovesStoredKey()
    {
        var sut = BuildSut();
        await sut.SaveAsync("k", CancellationToken.None);

        await sut.ClearAsync(CancellationToken.None);

        File.Exists(_storePath).Should().BeFalse();
        (await sut.LoadAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Clear_WhenNothingStored_DoesNotThrow()
    {
        var sut = BuildSut();
        Func<Task> act = () => sut.ClearAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Save_StoredBytesAreNotPlaintext_OnWindows()
    {
        const string key = "plaintext-api-key-value";
        var sut = BuildSut();
        await sut.SaveAsync(key, CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(_storePath);

        if (OperatingSystem.IsWindows())
        {
            var plain = Encoding.UTF8.GetBytes(key);
            bytes.Should().NotEqual(plain);
        }
    }

    [Fact]
    public void Ctor_NullStorePath_Throws()
    {
        Action act = () => new DpapiDeviceKeyStore(null!, new SystemClock());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullClock_Throws()
    {
        Action act = () => new DpapiDeviceKeyStore(_storePath, null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
