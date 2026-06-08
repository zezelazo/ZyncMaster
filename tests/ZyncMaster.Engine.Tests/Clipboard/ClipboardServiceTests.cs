using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests.Clipboard;

public sealed class ClipboardServiceTests
{
    private sealed class FakeCapture : IClipboardCaptureSource
    {
        public event Action<ClipboardEntry>? Captured;
        public bool Started;
        public bool Stopped;
        public void Start() => Started = true;
        public void Stop() => Stopped = true;
        public void Raise(ClipboardEntry e) => Captured?.Invoke(e);
    }

    private sealed class FakeTransport : IClipboardTransport
    {
        public readonly List<ClipboardEntry> Published = new();

        public Task PublishAsync(ClipboardEntry encrypted, CancellationToken ct = default)
        {
            Published.Add(encrypted);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ClipboardEntry>> GetHistoryAsync(CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<ClipboardEntry>)Array.Empty<ClipboardEntry>());
        public Task<ClipboardSettings> GetSettingsAsync(string deviceId, CancellationToken ct = default) =>
            Task.FromResult(new ClipboardSettings());
        public Task UpdateSettingsAsync(string deviceId, ClipboardSettings s, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RelayKeyAsync(string fromDeviceId, string targetDeviceId, byte[] wrappedKey, CancellationToken ct = default) =>
            Task.FromResult(true);
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public event Action<ClipboardEntry>? ItemReceived;
        public event Action<string, byte[]>? KeyReceived;
        public event Action<IReadOnlyList<string>> PresenceChanged { add { } remove { } }
        public event Action PresenceReset { add { } remove { } }
        public event Action<string, ClipboardSettings> SettingsChanged { add { } remove { } }
        public void RaiseItem(ClipboardEntry e) => ItemReceived?.Invoke(e);
        public void RaiseKey(string from, byte[] wrapped) => KeyReceived?.Invoke(from, wrapped);
    }

    private sealed class FakeSink : IClipboardSink
    {
        public readonly List<ClipboardEntry> Set = new();
        public readonly List<ClipboardEntry> Pasted = new();
        public bool PasteResult = true;
        public Task SetAsync(ClipboardEntry entry, CancellationToken ct = default)
        {
            Set.Add(entry);
            return Task.CompletedTask;
        }
        public Task<bool> PasteIntoFocusedAsync(ClipboardEntry entry, CancellationToken ct = default)
        {
            Pasted.Add(entry);
            return Task.FromResult(PasteResult);
        }
    }

    private sealed class FakeKeyStore : IClipboardKeyStore
    {
        private byte[]? _textKey;
        private RSA? _rsa;
        public FakeKeyStore(byte[]? textKey = null) => _textKey = textKey;

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

    private sealed class Harness
    {
        public FakeCapture Capture = new();
        public FakeTransport Transport = new();
        public FakeSink Sink = new();
        public FakeKeyStore Keys;
        public ClipboardSettings Settings = new();
        public ClipboardService Service;

        public Harness(byte[]? textKey, long hardMax = 1_000_000)
        {
            Keys = new FakeKeyStore(textKey);
            var keyExchange = new ClipboardKeyExchange(Keys, Transport);
            var dedupe = new ClipboardDedupe();
            Service = new ClipboardService(
                Capture, Transport, Sink, Keys, keyExchange, dedupe,
                () => Settings, hardMax);
        }
    }

    private static ClipboardEntry Text(string text) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = ClipboardEntryType.Text,
        Text = text,
        OriginDeviceId = "dev-a",
    };

    private static ClipboardEntry Image(byte[] bytes) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = ClipboardEntryType.Image,
        ImageBytes = bytes,
        OriginDeviceId = "dev-a",
    };

    private static async Task SettleAsync() => await Task.Delay(50);

    [Fact]
    public async Task Captured_Text_IsEncryptedAndPublished_NotInClear()
    {
        var key = TextCrypto.NewKey();
        var h = new Harness(key);

        h.Capture.Raise(Text("secret message"));
        await SettleAsync();

        h.Transport.Published.Should().HaveCount(1);
        var published = h.Transport.Published[0];
        published.CipherText.Should().NotBeNull();
        published.Text.Should().BeNull();
        TextCrypto.Decrypt(key, published.CipherText!).Should().Be("secret message");
    }

    [Fact]
    public async Task Captured_IgnoredWhenSendFalse()
    {
        var h = new Harness(TextCrypto.NewKey()) { };
        h.Settings = new ClipboardSettings { Send = false };
        // Recreate service so the settings delegate reflects the new value (delegate captures field).
        // The harness delegate is () => Settings, so reassigning the field is enough.

        h.Capture.Raise(Text("nope"));
        await SettleAsync();

        h.Transport.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Captured_NoTextKey_TextNotPublished()
    {
        var h = new Harness(textKey: null);

        h.Capture.Raise(Text("cannot encrypt yet"));
        await SettleAsync();

        h.Transport.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Echo_CaptureAfterAutoSet_NotPublished()
    {
        var key = TextCrypto.NewKey();
        var h = new Harness(key);

        // Simulate a receive that auto-sets to the OS clipboard (this MarkApplied's the content).
        var cipher = TextCrypto.Encrypt(key, "round trip");
        var received = new ClipboardEntry
        {
            Id = "r1",
            Type = ClipboardEntryType.Text,
            CipherText = cipher,
            OriginDeviceId = "dev-b",
        };
        h.Transport.RaiseItem(received);
        await SettleAsync();
        h.Sink.Set.Should().HaveCount(1);

        // The OS now fires a capture for that very content -> must be recognized as echo, not published.
        h.Capture.Raise(Text("round trip"));
        await SettleAsync();

        h.Transport.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task PasteAsync_MarksDedupe_SoEchoCaptureNotPublished()
    {
        var key = TextCrypto.NewKey();
        var h = new Harness(key);

        // User pastes a (decrypted) history item. PasteAsync must mark the dedupe before the OS write
        // so the WM_CLIPBOARDUPDATE the write triggers is suppressed as our own echo.
        var wrote = await h.Service.PasteAsync(Text("pasted text"));
        wrote.Should().BeTrue();
        h.Sink.Pasted.Should().HaveCount(1);
        h.Sink.Pasted[0].Text.Should().Be("pasted text");

        // The OS fires a capture for that very content -> must be dropped, not re-published.
        h.Capture.Raise(Text("pasted text"));
        await SettleAsync();

        h.Transport.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task PasteAsync_ReturnsSinkResult_WhenNothingWritten()
    {
        var h = new Harness(TextCrypto.NewKey());
        h.Sink.PasteResult = false;

        var wrote = await h.Service.PasteAsync(Text("nothing"));

        wrote.Should().BeFalse();
    }

    [Fact]
    public async Task Received_Text_DecryptedAndAutoSet()
    {
        var key = TextCrypto.NewKey();
        var h = new Harness(key);

        var cipher = TextCrypto.Encrypt(key, "hello from peer");
        h.Transport.RaiseItem(new ClipboardEntry
        {
            Id = "r1",
            Type = ClipboardEntryType.Text,
            CipherText = cipher,
            OriginDeviceId = "dev-b",
        });
        await SettleAsync();

        h.Sink.Set.Should().HaveCount(1);
        h.Sink.Set[0].Text.Should().Be("hello from peer");
    }

    [Fact]
    public async Task Received_Text_AutoSyncOff_DecryptsButNotSet()
    {
        var key = TextCrypto.NewKey();
        var h = new Harness(key);
        // Receive stays on (default) so the entry IS decrypted; AutoSync off means it must NOT be
        // written to the OS clipboard — the viewer holds it for an explicit user paste instead.
        h.Settings = new ClipboardSettings { AutoSync = false, Receive = true };

        h.Transport.RaiseItem(new ClipboardEntry
        {
            Id = "r1",
            Type = ClipboardEntryType.Text,
            CipherText = TextCrypto.Encrypt(key, "manual only"),
            OriginDeviceId = "dev-b",
        });
        await SettleAsync();

        h.Sink.Set.Should().BeEmpty();
    }

    [Fact]
    public async Task Received_IgnoredWhenReceiveFalse()
    {
        var key = TextCrypto.NewKey();
        var h = new Harness(key);
        h.Settings = new ClipboardSettings { Receive = false };

        h.Transport.RaiseItem(new ClipboardEntry
        {
            Id = "r1",
            Type = ClipboardEntryType.Text,
            CipherText = TextCrypto.Encrypt(key, "x"),
            OriginDeviceId = "dev-b",
        });
        await SettleAsync();

        h.Sink.Set.Should().BeEmpty();
    }

    [Fact]
    public async Task Received_Text_NoKey_NotSet()
    {
        var h = new Harness(textKey: null);

        h.Transport.RaiseItem(new ClipboardEntry
        {
            Id = "r1",
            Type = ClipboardEntryType.Text,
            CipherText = new byte[] { 1, 2, 3, 4 },
            OriginDeviceId = "dev-b",
        });
        await SettleAsync();

        h.Sink.Set.Should().BeEmpty();
    }

    [Fact]
    public async Task Captured_ImageOverHardMax_Skipped()
    {
        var h = new Harness(TextCrypto.NewKey(), hardMax: 10);

        h.Capture.Raise(Image(new byte[100]));
        await SettleAsync();

        h.Transport.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Captured_ImageWithinLimit_Published()
    {
        var h = new Harness(TextCrypto.NewKey(), hardMax: 1000);

        h.Capture.Raise(Image(new byte[100]));
        await SettleAsync();

        h.Transport.Published.Should().HaveCount(1);
        h.Transport.Published[0].SizeBytes.Should().Be(100);
    }

    [Fact]
    public async Task Received_Image_AutoSet()
    {
        var h = new Harness(TextCrypto.NewKey());

        h.Transport.RaiseItem(Image(new byte[50]));
        await SettleAsync();

        h.Sink.Set.Should().HaveCount(1);
        h.Sink.Set[0].Type.Should().Be(ClipboardEntryType.Image);
    }

    [Fact]
    public async Task KeyReceived_RelaysToKeyExchange_SavesKey()
    {
        var h = new Harness(textKey: null);
        var (ourPub, _) = await h.Keys.EnsureDeviceKeypairAsync();
        var shared = TextCrypto.NewKey();
        var wrapped = KeyWrap.Wrap(ourPub, shared);

        h.Transport.RaiseKey("peer-dev", wrapped);
        await SettleAsync();

        (await h.Keys.LoadTextKeyAsync()).Should().BeEquivalentTo(shared);
    }

    [Fact]
    public void StartStop_ForwardsToCapture()
    {
        var h = new Harness(TextCrypto.NewKey());

        h.Service.Start();
        h.Capture.Started.Should().BeTrue();

        h.Service.Stop();
        h.Capture.Stopped.Should().BeTrue();
    }
}
