using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests.Clipboard;

public sealed class ClipboardKeyExchangeTests
{
    // In-memory key store. EnsureDeviceKeypairAsync returns a stable per-instance RSA keypair so a
    // peer can wrap the text key against this device's public key and we can later unwrap it.
    private sealed class FakeKeyStore : IClipboardKeyStore
    {
        private byte[]? _textKey;
        private RSA? _rsa;

        public Task<byte[]?> LoadTextKeyAsync(CancellationToken ct = default) =>
            Task.FromResult(_textKey is null ? null : (byte[])_textKey.Clone());

        public Task SaveTextKeyAsync(byte[] key, CancellationToken ct = default)
        {
            _textKey = (byte[])key.Clone();
            return Task.CompletedTask;
        }

        public Task<(byte[] publicKey, RSA privateKey)> EnsureDeviceKeypairAsync(CancellationToken ct = default)
        {
            _rsa ??= RSA.Create(2048);
            return Task.FromResult((KeyWrap.ExportPublicKey(_rsa), _rsa));
        }
    }

    private sealed class FakeTransport : IClipboardTransport
    {
        public string? LastFrom;
        public string? LastTarget;
        public byte[]? LastWrapped;
        public int RelayCalls;

        public Task PublishAsync(ClipboardEntry encrypted, CancellationToken ct = default) => Task.CompletedTask;
        public Task<System.Collections.Generic.IReadOnlyList<ClipboardEntry>> GetHistoryAsync(CancellationToken ct = default) =>
            Task.FromResult((System.Collections.Generic.IReadOnlyList<ClipboardEntry>)Array.Empty<ClipboardEntry>());
        public Task DeleteEntryAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClipboardSettings> GetSettingsAsync(string deviceId, CancellationToken ct = default) =>
            Task.FromResult(new ClipboardSettings());
        public Task UpdateSettingsAsync(string deviceId, ClipboardSettings s, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> RelayKeyAsync(string fromDeviceId, string targetDeviceId, byte[] wrappedKey, CancellationToken ct = default)
        {
            RelayCalls++;
            LastFrom = fromDeviceId;
            LastTarget = targetDeviceId;
            LastWrapped = wrappedKey;
            return Task.FromResult(true);
        }

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public event Action<ClipboardEntry> ItemReceived { add { } remove { } }
        public event Action<string, byte[]> KeyReceived { add { } remove { } }
        public event Action<string> DeletedReceived { add { } remove { } }
        public event Action<IReadOnlyList<string>> PresenceChanged { add { } remove { } }
        public event Action PresenceReset { add { } remove { } }
        public event Action<string, ClipboardSettings> SettingsChanged { add { } remove { } }
    }

    [Fact]
    public async Task EnsureTextKey_FirstDevice_GeneratesAndSaves()
    {
        var keys = new FakeKeyStore();
        var sut = new ClipboardKeyExchange(keys, new FakeTransport());

        var key = await sut.EnsureTextKeyAsync(historyIsEmpty: true, CancellationToken.None);

        key.Should().NotBeNull();
        key!.Length.Should().Be(32);
        (await keys.LoadTextKeyAsync()).Should().BeEquivalentTo(key);
    }

    [Fact]
    public async Task EnsureTextKey_ExistingKey_ReturnsItWithoutGenerationOrRelay()
    {
        var keys = new FakeKeyStore();
        var existing = TextCrypto.NewKey();
        await keys.SaveTextKeyAsync(existing);
        var transport = new FakeTransport();
        var sut = new ClipboardKeyExchange(keys, transport);

        var key = await sut.EnsureTextKeyAsync(historyIsEmpty: false, CancellationToken.None);

        key.Should().BeEquivalentTo(existing);
        transport.RelayCalls.Should().Be(0);
    }

    [Fact]
    public async Task EnsureTextKey_NotFirst_NoKey_HistoryNotEmpty_ReturnsNull()
    {
        var keys = new FakeKeyStore();
        var sut = new ClipboardKeyExchange(keys, new FakeTransport());

        var key = await sut.EnsureTextKeyAsync(historyIsEmpty: false, CancellationToken.None);

        key.Should().BeNull();
        (await keys.LoadTextKeyAsync()).Should().BeNull();
    }

    [Fact]
    public async Task AdmitDevice_WrapsOurKeyForTarget_RelaysUnwrappableBlob()
    {
        var keys = new FakeKeyStore();
        var ourKey = TextCrypto.NewKey();
        await keys.SaveTextKeyAsync(ourKey);
        var transport = new FakeTransport();
        var sut = new ClipboardKeyExchange(keys, transport);

        // The target device's keypair.
        using var targetRsa = RSA.Create(2048);
        var targetPub = KeyWrap.ExportPublicKey(targetRsa);

        await sut.AdmitDeviceAsync("target-dev", targetPub, "me-dev", CancellationToken.None);

        transport.RelayCalls.Should().Be(1);
        transport.LastFrom.Should().Be("me-dev");
        transport.LastTarget.Should().Be("target-dev");
        transport.LastWrapped.Should().NotBeNull();

        // The target's private key recovers exactly our text key.
        var recovered = KeyWrap.Unwrap(targetRsa, transport.LastWrapped!);
        recovered.Should().BeEquivalentTo(ourKey);
    }

    [Fact]
    public async Task OnKeyReceived_UnwrapsWithDevicePrivateKey_AndSavesPeerKey()
    {
        var keys = new FakeKeyStore();
        var sut = new ClipboardKeyExchange(keys, new FakeTransport());

        // Our device's public key, as a peer would obtain it.
        var (ourPub, _) = await keys.EnsureDeviceKeypairAsync();

        // A peer wraps the shared text key against OUR public key.
        var sharedKey = TextCrypto.NewKey();
        var wrapped = KeyWrap.Wrap(ourPub, sharedKey);

        await sut.OnKeyReceivedAsync("peer-dev", wrapped, CancellationToken.None);

        (await keys.LoadTextKeyAsync()).Should().BeEquivalentTo(sharedKey);
    }
}
