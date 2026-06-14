using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Core;
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
        public readonly List<ClipboardSettings> UpdatedSettings = new();
        public Exception? PublishError;

        public Task PublishAsync(ClipboardEntry encrypted, CancellationToken ct = default)
        {
            if (PublishError is not null)
                throw PublishError;
            Published.Add(encrypted);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ClipboardEntry>> GetHistoryAsync(CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<ClipboardEntry>)Array.Empty<ClipboardEntry>());
        public Task DeleteEntryAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClipboardSettings> GetSettingsAsync(string deviceId, CancellationToken ct = default) =>
            Task.FromResult(new ClipboardSettings());
        public Task UpdateSettingsAsync(string deviceId, ClipboardSettings s, CancellationToken ct = default)
        {
            UpdatedSettings.Add(s);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ClipboardDeviceKeyInfo>> GetDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<ClipboardDeviceKeyInfo>)Array.Empty<ClipboardDeviceKeyInfo>());
        public Task<bool> RelayKeyAsync(string fromDeviceId, string targetDeviceId, byte[] wrappedKey, CancellationToken ct = default) =>
            Task.FromResult(true);
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public event Action<ClipboardEntry>? ItemReceived;
        public event Action<string, byte[]>? KeyReceived;
        public event Action<string> DeletedReceived { add { } remove { } }
        public event Action<IReadOnlyList<string>> PresenceChanged { add { } remove { } }
        public event Action PresenceReset { add { } remove { } }
        public event Action<string, ClipboardSettings> SettingsChanged { add { } remove { } }
        public event Action<string, string, string> PairRunReceived { add { } remove { } }
        public event Action PairsChanged { add { } remove { } }
        public void RaiseItem(ClipboardEntry e) => ItemReceived?.Invoke(e);
        public void RaiseKey(string from, byte[] wrapped) => KeyReceived?.Invoke(from, wrapped);
    }

    private sealed class FakeSink : IClipboardSink
    {
        public readonly List<ClipboardEntry> Set = new();
        public readonly List<ClipboardEntry> Pasted = new();
        public readonly List<nint> PasteTargets = new();
        public bool PasteResult = true;
        public Exception? SetError;
        public Task SetAsync(ClipboardEntry entry, CancellationToken ct = default)
        {
            if (SetError is not null)
                throw SetError;
            Set.Add(entry);
            return Task.CompletedTask;
        }
        public Task<bool> PasteIntoFocusedAsync(ClipboardEntry entry, nint targetWindow, CancellationToken ct = default)
        {
            Pasted.Add(entry);
            PasteTargets.Add(targetWindow);
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

    private sealed class FakeAppLogger : IAppLogger
    {
        public readonly List<(LogLevel Level, string Message, Exception? Ex)> Entries = new();
        private readonly object _gate = new();
        public void Log(LogLevel level, string message, Exception? ex = null)
        {
            lock (_gate) Entries.Add((level, message, ex));
        }
        public bool IsEnabled(LogLevel level) => true;

        public List<(LogLevel Level, string Message, Exception? Ex)> Warnings
        {
            get { lock (_gate) return Entries.FindAll(e => e.Level == LogLevel.Warning); }
        }
    }

    private sealed class Harness
    {
        public FakeCapture Capture = new();
        public FakeTransport Transport = new();
        public FakeSink Sink = new();
        public FakeKeyStore Keys;
        public FakeAppLogger Logger = new();
        public ClipboardSettings Settings = new();
        public ClipboardService Service;

        public Harness(
            byte[]? textKey, long hardMax = 1_000_000, string? deviceId = "this-dev",
            TimeProvider? timeProvider = null)
        {
            Keys = new FakeKeyStore(textKey);
            var keyExchange = new ClipboardKeyExchange(Keys, Transport, () => deviceId);
            var dedupe = new ClipboardDedupe(timeProvider: timeProvider);
            Service = new ClipboardService(
                Capture, Transport, Sink, Keys, keyExchange, dedupe,
                () => Settings, hardMax, Logger);
        }
    }

    // Manual monotonic clock for the dedupe TTL windows; advanced explicitly by the test.
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan by) => _timestamp += by.Ticks;
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
        // Send-off is the user's own choice — never noisier than Debug.
        h.Logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Captured_NoTextKey_TextNotPublished_AndWarns()
    {
        var h = new Harness(textKey: null);

        h.Capture.Raise(Text("cannot encrypt yet"));
        await SettleAsync();

        h.Transport.Published.Should().BeEmpty();
        h.Logger.Warnings.Should().ContainSingle()
            .Which.Message.Should().Contain("text key").And.Contain("not been admitted");
    }

    [Fact]
    public async Task Captured_Text_PublishThrows_LogsWarningWithReason_DoesNotCrash()
    {
        var h = new Harness(TextCrypto.NewKey());
        h.Transport.PublishError = new InvalidOperationException(
            "Clipboard request POST https://x/api/clipboard/items failed with status 413: too large");

        h.Capture.Raise(Text("doomed"));
        await SettleAsync();

        var warning = h.Logger.Warnings.Should().ContainSingle().Which;
        warning.Message.Should().Contain("publish failed").And.Contain("413");
        warning.Ex.Should().BeSameAs(h.Transport.PublishError);
    }

    [Fact]
    public async Task Captured_Image_PublishThrows_LogsWarningWithReason_DoesNotCrash()
    {
        var h = new Harness(TextCrypto.NewKey(), hardMax: 1000);
        h.Transport.PublishError = new InvalidOperationException(
            "Clipboard request POST https://x/api/clipboard/items failed with status 413: too large");

        h.Capture.Raise(Image(new byte[500]));
        await SettleAsync();

        var warning = h.Logger.Warnings.Should().ContainSingle().Which;
        warning.Message.Should().Contain("publish failed").And.Contain("413");
        warning.Ex.Should().BeSameAs(h.Transport.PublishError);
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
        // Echo suppression is the normal case after every apply — never a Warning.
        h.Logger.Warnings.Should().BeEmpty();
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
    public async Task PasteAsync_ForwardsTargetWindow_ToSink()
    {
        // The viewer captures the user's real foreground window BEFORE it opens; the paste must aim
        // the synthetic Ctrl+V at THAT handle (at paste time the viewer itself is foreground).
        var h = new Harness(TextCrypto.NewKey());

        var wrote = await h.Service.PasteAsync(Text("targeted"), targetWindow: 0x1234);

        wrote.Should().BeTrue();
        h.Sink.PasteTargets.Should().ContainSingle().Which.Should().Be((nint)0x1234);
    }

    [Fact]
    public async Task PasteAsync_DefaultsTargetWindow_ToZero()
    {
        var h = new Harness(TextCrypto.NewKey());

        await h.Service.PasteAsync(Text("untargeted"));

        h.Sink.PasteTargets.Should().ContainSingle().Which.Should().Be((nint)0);
    }

    [Fact]
    public async Task CopyAsync_WritesClipboardOnly_NeverPastes()
    {
        // Copy-only (the dashboard Copy button): the OS clipboard is set, but no focus change and no
        // synthetic Ctrl+V — the sink's paste path must never run.
        var h = new Harness(TextCrypto.NewKey());

        await h.Service.CopyAsync(Text("copy me"));

        h.Sink.Set.Should().ContainSingle().Which.Text.Should().Be("copy me");
        h.Sink.Pasted.Should().BeEmpty();
    }

    [Fact]
    public async Task CopyAsync_MarksDedupe_SoEchoCaptureNotPublished()
    {
        var key = TextCrypto.NewKey();
        var h = new Harness(key);

        // User copies a history item back to the OS clipboard. CopyAsync must mark the dedupe before
        // the write so the WM_CLIPBOARDUPDATE the write triggers is suppressed as our own echo.
        await h.Service.CopyAsync(Text("copied text"));

        h.Capture.Raise(Text("copied text"));
        await SettleAsync();

        h.Transport.Published.Should().BeEmpty();
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
    public async Task Received_Text_NoKey_NotSet_AndWarns()
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
        h.Logger.Warnings.Should().ContainSingle()
            .Which.Message.Should().Contain("cannot be decrypted yet").And.Contain("text key");
    }

    [Fact]
    public async Task Received_SinkThrows_LogsWarning_DoesNotCrash()
    {
        var h = new Harness(TextCrypto.NewKey());
        h.Sink.SetError = new InvalidOperationException("clipboard is locked by another process");

        h.Transport.RaiseItem(Image(new byte[50]));
        await SettleAsync();

        var warning = h.Logger.Warnings.Should().ContainSingle().Which;
        warning.Message.Should().Contain("apply failed").And.Contain("locked by another process");
        warning.Ex.Should().BeSameAs(h.Sink.SetError);
    }

    [Fact]
    public async Task Captured_ImageOverHardMax_Skipped_AndWarnsWithSize()
    {
        var h = new Harness(TextCrypto.NewKey(), hardMax: 1000);

        h.Capture.Raise(Image(new byte[2000]));
        await SettleAsync();

        h.Transport.Published.Should().BeEmpty();
        h.Logger.Warnings.Should().ContainSingle()
            .Which.Message.Should().Contain("2000 bytes").And.Contain("exceeds");
    }

    [Fact]
    public async Task Captured_ImageWithinLimit_Published()
    {
        var h = new Harness(TextCrypto.NewKey(), hardMax: 1000);

        h.Capture.Raise(Image(new byte[500]));
        await SettleAsync();

        h.Transport.Published.Should().HaveCount(1);
        h.Transport.Published[0].SizeBytes.Should().Be(500);
    }

    [Fact]
    public async Task Captured_TinyImage_DroppedAsBareHeader_NoWarning()
    {
        // A 76-byte CF_DIB is a header with no pixel data — clipboard noise, not a picture. It must
        // be dropped quietly (Debug), never published, never warned about.
        var h = new Harness(TextCrypto.NewKey());

        h.Capture.Raise(Image(new byte[76]));
        await SettleAsync();

        h.Transport.Published.Should().BeEmpty();
        h.Logger.Warnings.Should().BeEmpty();
        h.Logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Debug && e.Message.Contains("below the") && e.Message.Contains("minimum"));
    }

    [Fact]
    public async Task Captured_SameTextRepeatedQuickly_PublishedOnlyOnce()
    {
        // Windows fires WM_CLIPBOARDUPDATE 2-3 times for a single copy, and users mash Ctrl+C on
        // the same content. All captures of identical content inside the recent-publish window
        // must collapse into ONE publish — this is half of the cross-machine echo-loop fix.
        var h = new Harness(TextCrypto.NewKey());

        h.Capture.Raise(Text("same content"));
        await SettleAsync();
        h.Capture.Raise(Text("same content"));
        h.Capture.Raise(Text("same content"));
        await SettleAsync();

        h.Transport.Published.Should().HaveCount(1);
        h.Logger.Warnings.Should().BeEmpty(); // duplicate drops are Debug noise, not failures
    }

    [Fact]
    public async Task Captured_SameImageRepeatedQuickly_PublishedOnlyOnce()
    {
        var h = new Harness(TextCrypto.NewKey());
        var bytes = new byte[5000];
        bytes[0] = 42;

        h.Capture.Raise(Image(bytes));
        await SettleAsync();
        h.Capture.Raise(Image((byte[])bytes.Clone()));
        h.Capture.Raise(Image((byte[])bytes.Clone()));
        await SettleAsync();

        h.Transport.Published.Should().HaveCount(1);
    }

    [Fact]
    public async Task Captured_SameTextAgainAfterPublishWindow_PublishedAgain()
    {
        var time = new ManualTimeProvider();
        var h = new Harness(TextCrypto.NewKey(), timeProvider: time);

        h.Capture.Raise(Text("re-copied later"));
        await SettleAsync();
        h.Transport.Published.Should().HaveCount(1);

        // Within the window: dropped.
        time.Advance(TimeSpan.FromSeconds(5));
        h.Capture.Raise(Text("re-copied later"));
        await SettleAsync();
        h.Transport.Published.Should().HaveCount(1);

        // After the window: a genuine re-copy, published again.
        time.Advance(ClipboardDedupe.RecentPublishTtl + TimeSpan.FromSeconds(1));
        h.Capture.Raise(Text("re-copied later"));
        await SettleAsync();
        h.Transport.Published.Should().HaveCount(2);
    }

    [Fact]
    public async Task Captured_AlternatingContent_ABA_AllThreePublished()
    {
        // The interleaved re-copy regression: copy A, copy B, copy A again — all inside the
        // recent-publish TTL. The old multi-entry publish map still flagged A as "recently
        // published" and silently dropped the third copy, leaving the peers on B while the local
        // clipboard held A. Every alternation is a deliberate user action and must be published.
        var key = TextCrypto.NewKey();
        var h = new Harness(key);

        h.Capture.Raise(Text("content A"));
        await SettleAsync();
        h.Capture.Raise(Text("content B"));
        await SettleAsync();
        h.Capture.Raise(Text("content A"));
        await SettleAsync();

        h.Transport.Published.Should().HaveCount(3);
        TextCrypto.Decrypt(key, h.Transport.Published[2].CipherText!).Should().Be("content A");
        h.Logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Echo_RecopyOfAppliedContent_AfterCopyingSomethingElse_IsPublished()
    {
        // Echo-window variant of the same regression: a peer item A is applied (opening A's echo
        // window), the user copies B, then deliberately re-copies A while A's 15s window would
        // still be open. Capturing B proves A's apply burst is over, so the re-copy of A must be
        // published, not swallowed as an echo.
        var key = TextCrypto.NewKey();
        var h = new Harness(key);

        h.Transport.RaiseItem(new ClipboardEntry
        {
            Id = "r1",
            Type = ClipboardEntryType.Text,
            CipherText = TextCrypto.Encrypt(key, "peer content A"),
            OriginDeviceId = "dev-b",
        });
        await SettleAsync();
        h.Sink.Set.Should().HaveCount(1);

        h.Capture.Raise(Text("local content B"));
        await SettleAsync();
        h.Transport.Published.Should().HaveCount(1);

        h.Capture.Raise(Text("peer content A"));
        await SettleAsync();

        h.Transport.Published.Should().HaveCount(2);
        TextCrypto.Decrypt(key, h.Transport.Published[1].CipherText!).Should().Be("peer content A");
    }

    [Fact]
    public async Task Echo_MultiFireCaptureAfterApply_AllSuppressed()
    {
        // One programmatic clipboard set fires WM_CLIPBOARDUPDATE several times (more under RDP
        // clipboard redirection). Every one of those captures must be suppressed as the same echo —
        // the old consume-once behaviour let the second fire re-publish and start the ping-pong loop.
        var key = TextCrypto.NewKey();
        var time = new ManualTimeProvider();
        var h = new Harness(key, timeProvider: time);

        h.Transport.RaiseItem(new ClipboardEntry
        {
            Id = "r1",
            Type = ClipboardEntryType.Text,
            CipherText = TextCrypto.Encrypt(key, "looping content"),
            OriginDeviceId = "dev-b",
        });
        await SettleAsync();
        h.Sink.Set.Should().HaveCount(1);

        // Three OS fires for the single apply, spread over a couple of seconds.
        h.Capture.Raise(Text("looping content"));
        time.Advance(TimeSpan.FromMilliseconds(300));
        h.Capture.Raise(Text("looping content"));
        time.Advance(TimeSpan.FromSeconds(2));
        h.Capture.Raise(Text("looping content"));
        await SettleAsync();

        h.Transport.Published.Should().BeEmpty();
        h.Logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Echo_SuppressionExpires_UserRecopyAfterTtl_IsPublished()
    {
        var key = TextCrypto.NewKey();
        var time = new ManualTimeProvider();
        var h = new Harness(key, timeProvider: time);

        h.Transport.RaiseItem(new ClipboardEntry
        {
            Id = "r1",
            Type = ClipboardEntryType.Text,
            CipherText = TextCrypto.Encrypt(key, "old content"),
            OriginDeviceId = "dev-b",
        });
        await SettleAsync();

        time.Advance(ClipboardDedupe.AppliedEchoTtl + TimeSpan.FromSeconds(1));

        // Long after the apply, the user deliberately copies the same content: not an echo anymore.
        h.Capture.Raise(Text("old content"));
        await SettleAsync();

        h.Transport.Published.Should().HaveCount(1);
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
    public async Task KeyReceived_MalformedBlob_DoesNotCrash_LogsWarning_SavesNothing()
    {
        // A garbage wrapped blob makes KeyWrap.Unwrap throw CryptographicException inside the
        // async-void KeyReceived handler. Unhandled, that terminates the whole process (the same
        // failure mode as the 413-on-image crash) — the boundary catch must absorb it and log.
        var h = new Harness(textKey: null);
        await h.Keys.EnsureDeviceKeypairAsync();

        h.Transport.RaiseKey("peer-dev", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        await SettleAsync();

        (await h.Keys.LoadTextKeyAsync()).Should().BeNull();
        h.Logger.Warnings.Should().Contain(w => w.Message.Contains("could not be processed"));
    }

    [Fact]
    public async Task Received_WrongKey_ThreeConsecutiveFailures_ReAdvertisesNeedForTheKey()
    {
        // Our key is NOT the one the sender used (the classic both-sides-self-generated split brain).
        var h = new Harness(TextCrypto.NewKey());
        var senderKey = TextCrypto.NewKey();

        for (var i = 0; i < 3; i++)
            h.Transport.RaiseItem(new ClipboardEntry
            {
                Id = $"r{i}",
                Type = ClipboardEntryType.Text,
                CipherText = TextCrypto.Encrypt(senderKey, $"unreadable {i}"),
                OriginDeviceId = "dev-b",
            });

        // The third failure marks the key suspect -> RequestKeyAsync upserts needsTextKey=true. The
        // receive handler is fire-and-forget, so poll instead of a fixed delay.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (h.Transport.UpdatedSettings.Count == 0 && sw.ElapsedMilliseconds < 5000)
            await Task.Delay(20);

        h.Transport.UpdatedSettings.Should().ContainSingle();
        h.Transport.UpdatedSettings[0].NeedsTextKey.Should().BeTrue();
        h.Transport.UpdatedSettings[0].PublicKeyBase64.Should().NotBeNullOrEmpty();
        h.Sink.Set.Should().BeEmpty(); // nothing undecryptable ever reaches the OS clipboard
        // Each undecryptable item must leave a trace in the log, not vanish.
        h.Logger.Warnings.Should().Contain(w => w.Message.Contains("could not be decrypted"));
    }

    [Fact]
    public async Task Received_DecryptSuccessBetweenFailures_ResetsTheSuspectStreak()
    {
        var key = TextCrypto.NewKey();
        var h = new Harness(key);
        var wrongKey = TextCrypto.NewKey();

        ClipboardEntry Bad(int i) => new()
        {
            Id = $"bad{i}",
            Type = ClipboardEntryType.Text,
            CipherText = TextCrypto.Encrypt(wrongKey, "nope"),
            OriginDeviceId = "dev-b",
        };

        h.Transport.RaiseItem(Bad(0));
        h.Transport.RaiseItem(Bad(1));
        await SettleAsync();
        h.Transport.RaiseItem(new ClipboardEntry
        {
            Id = "good",
            Type = ClipboardEntryType.Text,
            CipherText = TextCrypto.Encrypt(key, "readable"),
            OriginDeviceId = "dev-b",
        });
        await SettleAsync();
        h.Transport.RaiseItem(Bad(2));
        h.Transport.RaiseItem(Bad(3));
        await SettleAsync();

        // Never 3 consecutive failures -> the key is never marked suspect, no re-request.
        h.Transport.UpdatedSettings.Should().BeEmpty();
    }

    // ---------- ItemPublished (the local-mirror event: the server echo excludes the origin, so the
    // host relies on this to show the user's OWN copies in the open UI lists) ----------

    [Fact]
    public async Task Captured_Text_Published_RaisesItemPublished_WithPlaintextAndOrigin()
    {
        var h = new Harness(TextCrypto.NewKey());
        var raised = new List<ClipboardEntry>();
        h.Service.ItemPublished += raised.Add;

        h.Capture.Raise(Text("my own copy"));
        await SettleAsync();

        h.Transport.Published.Should().HaveCount(1);
        var mirrored = raised.Should().ContainSingle().Which;
        mirrored.Text.Should().Be("my own copy");   // the local mirror keeps the plaintext...
        mirrored.CipherText.Should().BeNull();      // ...only the transport sees the ciphertext
        mirrored.OriginDeviceId.Should().Be("dev-a");
    }

    [Fact]
    public async Task Captured_Image_Published_RaisesItemPublished_WithResolvedSize()
    {
        var h = new Harness(TextCrypto.NewKey(), hardMax: 1000);
        var raised = new List<ClipboardEntry>();
        h.Service.ItemPublished += raised.Add;

        h.Capture.Raise(Image(new byte[500]));
        await SettleAsync();

        raised.Should().ContainSingle().Which.SizeBytes.Should().Be(500);
    }

    [Fact]
    public async Task Captured_DuplicateInsidePublishWindow_RaisesItemPublished_OnlyOnce()
    {
        // The dedupe drops the duplicate publishes, so the mirror event must not fire for them
        // either — otherwise the UI would insert phantom twin rows the history does not contain.
        var h = new Harness(TextCrypto.NewKey());
        var raised = 0;
        h.Service.ItemPublished += _ => raised++;

        h.Capture.Raise(Text("same content"));
        await SettleAsync();
        h.Capture.Raise(Text("same content"));
        h.Capture.Raise(Text("same content"));
        await SettleAsync();

        h.Transport.Published.Should().HaveCount(1);
        raised.Should().Be(1);
    }

    [Fact]
    public async Task Captured_PublishThrows_DoesNotRaiseItemPublished()
    {
        // A failed publish never reached the shared history, so mirroring it into the local UI
        // would show an item the user's other devices will never receive.
        var h = new Harness(TextCrypto.NewKey());
        h.Transport.PublishError = new InvalidOperationException("413 too large");
        var raised = 0;
        h.Service.ItemPublished += _ => raised++;

        h.Capture.Raise(Text("doomed"));
        await SettleAsync();

        raised.Should().Be(0);
    }

    [Fact]
    public async Task Echo_CaptureAfterAutoSet_DoesNotRaiseItemPublished()
    {
        // The OS echo of a just-applied peer item is dropped before publishing — the mirror event
        // must stay silent too (the row already exists in every open list from the receive push).
        var key = TextCrypto.NewKey();
        var h = new Harness(key);
        var raised = 0;
        h.Service.ItemPublished += _ => raised++;

        h.Transport.RaiseItem(new ClipboardEntry
        {
            Id = "r1",
            Type = ClipboardEntryType.Text,
            CipherText = TextCrypto.Encrypt(key, "round trip"),
            OriginDeviceId = "dev-b",
        });
        await SettleAsync();
        h.Capture.Raise(Text("round trip"));
        await SettleAsync();

        raised.Should().Be(0);
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var keys = new FakeKeyStore();
        var transport = new FakeTransport();
        var keyExchange = new ClipboardKeyExchange(keys, transport, () => "this-dev");

        var act = () => new ClipboardService(
            new FakeCapture(), transport, new FakeSink(), keys, keyExchange,
            new ClipboardDedupe(), () => new ClipboardSettings(), 100, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
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
