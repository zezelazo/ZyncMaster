using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests.Clipboard;

// Covers the zero-touch key-admission orchestration on ClipboardKeyExchange: a needy device
// advertises needsTextKey + its public key via the settings upsert (RequestKeyAsync); a key-holder
// sweeps the roster and relays the wrapped key to each advertised peer exactly once per session
// (AdmitPendingPeersAsync); receipt clears the flag and raises TextKeyChanged; repeated decrypt
// failures mark the local key suspect and re-request a fresh copy.
public sealed class ClipboardKeyAdmissionTests
{
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

    // Recording transport: settings reads/writes, the devices roster and the key relays are all
    // configurable + observable, and the live broadcast events can be raised from tests.
    private sealed class FakeTransport : IClipboardTransport
    {
        public ClipboardSettings StoredSettings = new() { Send = false, Density = "mini" };
        public readonly List<(string deviceId, ClipboardSettings settings)> Upserts = new();
        public IReadOnlyList<ClipboardDeviceKeyInfo> Devices = Array.Empty<ClipboardDeviceKeyInfo>();
        public int GetDevicesCalls;
        public readonly List<(string from, string target, byte[] wrapped)> Relays = new();
        public Func<string, bool> DeliveredFor = _ => true;
        public Func<string, Exception?> RelayFailureFor = _ => null;
        public Exception? UpsertFailure;

        public Task PublishAsync(ClipboardEntry encrypted, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ClipboardEntry>> GetHistoryAsync(CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<ClipboardEntry>)Array.Empty<ClipboardEntry>());
        public Task DeleteEntryAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<ClipboardSettings> GetSettingsAsync(string deviceId, CancellationToken ct = default) =>
            Task.FromResult(StoredSettings);

        public Task UpdateSettingsAsync(string deviceId, ClipboardSettings s, CancellationToken ct = default)
        {
            if (UpsertFailure is not null)
                throw UpsertFailure;
            Upserts.Add((deviceId, s));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ClipboardDeviceKeyInfo>> GetDevicesAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref GetDevicesCalls);
            return Task.FromResult(Devices);
        }

        public Task<bool> RelayKeyAsync(string fromDeviceId, string targetDeviceId, byte[] wrappedKey, CancellationToken ct = default)
        {
            if (RelayFailureFor(targetDeviceId) is { } ex)
                throw ex;
            Relays.Add((fromDeviceId, targetDeviceId, wrappedKey));
            return Task.FromResult(DeliveredFor(targetDeviceId));
        }

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public event Action<ClipboardEntry> ItemReceived { add { } remove { } }
        public event Action<string, byte[]> KeyReceived { add { } remove { } }
        public event Action<string> DeletedReceived { add { } remove { } }
        public event Action<IReadOnlyList<string>>? PresenceChanged;
        public event Action PresenceReset { add { } remove { } }
        public event Action<string, ClipboardSettings>? SettingsChanged;
        public event Action<string, string, string> PairRunReceived { add { } remove { } }
        public event Action PairsChanged { add { } remove { } }

        public void RaisePresence(params string[] online) => PresenceChanged?.Invoke(online);
        public void RaiseSettings(string deviceId, ClipboardSettings s) => SettingsChanged?.Invoke(deviceId, s);
    }

    private static ClipboardDeviceKeyInfo Peer(string id, bool needsKey, byte[]? publicKey, bool online = true) => new()
    {
        DeviceId = id,
        Name = id,
        Online = online,
        NeedsTextKey = needsKey,
        PublicKeyBase64 = publicKey is null ? null : Convert.ToBase64String(publicKey),
    };

    // Polls until the condition holds (the admission sweeps triggered by broadcasts are
    // fire-and-forget, so the test must wait for the background task rather than a fixed delay).
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(20);
    }

    // ----- RequestKeyAsync -----

    [Fact]
    public async Task RequestKey_PublishesNeedAndPublicKey_PreservingStoredPreferences()
    {
        var keys = new FakeKeyStore();
        var transport = new FakeTransport { StoredSettings = new ClipboardSettings { Send = false, Density = "mini" } };
        var sut = new ClipboardKeyExchange(keys, transport, () => "me-dev");

        await sut.RequestKeyAsync();

        transport.Upserts.Should().ContainSingle();
        var (deviceId, settings) = transport.Upserts[0];
        deviceId.Should().Be("me-dev");
        settings.NeedsTextKey.Should().BeTrue();

        // The advertised key is OUR device public key, base64.
        var (ourPub, _) = await keys.EnsureDeviceKeypairAsync();
        settings.PublicKeyBase64.Should().Be(Convert.ToBase64String(ourPub));

        // Read-modify-write: the stored preferences survive the advertisement.
        settings.Send.Should().BeFalse();
        settings.Density.Should().Be("mini");
    }

    [Fact]
    public async Task RequestKey_NoDeviceIdYet_IsANoOp()
    {
        var transport = new FakeTransport();
        var sut = new ClipboardKeyExchange(new FakeKeyStore(), transport, () => "");

        await sut.RequestKeyAsync();

        transport.Upserts.Should().BeEmpty();
    }

    // ----- AdmitPendingPeersAsync -----

    [Fact]
    public async Task AdmitPendingPeers_RelaysToAdvertisedPeers_ExactlyOncePerSession()
    {
        var ourKey = TextCrypto.NewKey();
        var keys = new FakeKeyStore(ourKey);
        using var peerRsa = RSA.Create(2048);
        var peerPub = KeyWrap.ExportPublicKey(peerRsa);

        var transport = new FakeTransport
        {
            Devices = new[]
            {
                Peer("me-dev", needsKey: false, publicKey: null),          // self — skipped
                Peer("needy", needsKey: true, publicKey: peerPub),         // admitted
                Peer("settled", needsKey: false, publicKey: peerPub),      // no need — skipped
                Peer("keyless", needsKey: true, publicKey: null),          // no public key — skipped
            },
        };
        var sut = new ClipboardKeyExchange(keys, transport, () => "me-dev");

        var admitted = await sut.AdmitPendingPeersAsync();

        admitted.Should().Be(1);
        transport.Relays.Should().ContainSingle();
        transport.Relays[0].from.Should().Be("me-dev");
        transport.Relays[0].target.Should().Be("needy");

        // The relayed blob unwraps with the peer's private key to exactly our text key.
        KeyWrap.Unwrap(peerRsa, transport.Relays[0].wrapped).Should().BeEquivalentTo(ourKey);

        // A second sweep must not spam the already-admitted peer.
        (await sut.AdmitPendingPeersAsync()).Should().Be(0);
        transport.Relays.Should().HaveCount(1);
    }

    [Fact]
    public async Task AdmitPendingPeers_WithoutTextKey_DoesNotEvenFetchTheRoster()
    {
        var transport = new FakeTransport();
        var sut = new ClipboardKeyExchange(new FakeKeyStore(textKey: null), transport, () => "me-dev");

        var admitted = await sut.AdmitPendingPeersAsync();

        admitted.Should().Be(0);
        transport.GetDevicesCalls.Should().Be(0);
        transport.Relays.Should().BeEmpty();
    }

    [Fact]
    public async Task AdmitPendingPeers_UndeliveredRelay_RetriesOnTheNextSweep()
    {
        using var peerRsa = RSA.Create(2048);
        var transport = new FakeTransport
        {
            Devices = new[] { Peer("offline-peer", needsKey: true, publicKey: KeyWrap.ExportPublicKey(peerRsa), online: false) },
            DeliveredFor = _ => false, // peer not connected: the server could not deliver
        };
        var sut = new ClipboardKeyExchange(new FakeKeyStore(TextCrypto.NewKey()), transport, () => "me-dev");

        (await sut.AdmitPendingPeersAsync()).Should().Be(0);

        // The peer comes online; the same sweep retries because it was never marked admitted.
        transport.DeliveredFor = _ => true;
        (await sut.AdmitPendingPeersAsync()).Should().Be(1);
        transport.Relays.Should().HaveCount(2);
    }

    [Fact]
    public async Task AdmitPendingPeers_OnePeerFailing_DoesNotBlockTheOthers()
    {
        using var rsaA = RSA.Create(2048);
        using var rsaB = RSA.Create(2048);
        var transport = new FakeTransport
        {
            Devices = new[]
            {
                Peer("broken", needsKey: true, publicKey: KeyWrap.ExportPublicKey(rsaA)),
                Peer("healthy", needsKey: true, publicKey: KeyWrap.ExportPublicKey(rsaB)),
            },
            RelayFailureFor = target => target == "broken" ? new InvalidOperationException("relay down") : null,
        };
        var sut = new ClipboardKeyExchange(new FakeKeyStore(TextCrypto.NewKey()), transport, () => "me-dev");

        var admitted = await sut.AdmitPendingPeersAsync();

        admitted.Should().Be(1);
        transport.Relays.Should().ContainSingle();
        transport.Relays[0].target.Should().Be("healthy");
    }

    [Fact]
    public async Task AdmitPendingPeers_MalformedPeerPublicKey_SkippedWithoutBlockingOthers()
    {
        using var rsa = RSA.Create(2048);
        var transport = new FakeTransport
        {
            Devices = new[]
            {
                Peer("garbled", needsKey: true, publicKey: null) with { PublicKeyBase64 = "%%%not-base64%%%" },
                Peer("healthy", needsKey: true, publicKey: KeyWrap.ExportPublicKey(rsa)),
            },
        };
        var sut = new ClipboardKeyExchange(new FakeKeyStore(TextCrypto.NewKey()), transport, () => "me-dev");

        var admitted = await sut.AdmitPendingPeersAsync();

        admitted.Should().Be(1);
        transport.Relays.Should().ContainSingle();
        transport.Relays[0].target.Should().Be("healthy");
    }

    // ----- OnKeyReceivedAsync: flag clear + refresh event -----

    [Fact]
    public async Task OnKeyReceived_SavesKey_ClearsNeedFlag_AndRaisesTextKeyChanged()
    {
        var keys = new FakeKeyStore();
        var transport = new FakeTransport { StoredSettings = new ClipboardSettings { Receive = false, NeedsTextKey = true } };
        var sut = new ClipboardKeyExchange(keys, transport, () => "me-dev");

        var raised = 0;
        sut.TextKeyChanged += () => raised++;

        var (ourPub, _) = await keys.EnsureDeviceKeypairAsync();
        var shared = TextCrypto.NewKey();
        await sut.OnKeyReceivedAsync("holder-dev", KeyWrap.Wrap(ourPub, shared));

        (await keys.LoadTextKeyAsync()).Should().BeEquivalentTo(shared);
        raised.Should().Be(1);

        transport.Upserts.Should().ContainSingle();
        var (deviceId, settings) = transport.Upserts[0];
        deviceId.Should().Be("me-dev");
        settings.NeedsTextKey.Should().BeFalse();
        settings.Receive.Should().BeFalse(); // preferences preserved by the read-modify-write
    }

    [Fact]
    public async Task OnKeyReceived_FlagClearFailure_StillSavesKeyAndRaisesEvent()
    {
        var keys = new FakeKeyStore();
        var transport = new FakeTransport { UpsertFailure = new InvalidOperationException("server down") };
        var sut = new ClipboardKeyExchange(keys, transport, () => "me-dev");

        var raised = 0;
        sut.TextKeyChanged += () => raised++;

        var (ourPub, _) = await keys.EnsureDeviceKeypairAsync();
        var shared = TextCrypto.NewKey();
        await sut.OnKeyReceivedAsync("holder-dev", KeyWrap.Wrap(ourPub, shared));

        (await keys.LoadTextKeyAsync()).Should().BeEquivalentTo(shared);
        raised.Should().Be(1);
    }

    // ----- Wrong-key recovery: consecutive decrypt failures -----

    [Fact]
    public async Task ReportDecryptFailure_BelowThreshold_DoesNotReRequest()
    {
        var transport = new FakeTransport();
        var sut = new ClipboardKeyExchange(new FakeKeyStore(TextCrypto.NewKey()), transport, () => "me-dev");

        (await sut.ReportDecryptFailureAsync()).Should().BeFalse();
        (await sut.ReportDecryptFailureAsync()).Should().BeFalse();

        transport.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task ReportDecryptFailure_ThirdConsecutive_TriggersReRequest_AndResetsTheStreak()
    {
        var keys = new FakeKeyStore(TextCrypto.NewKey()); // we HOLD a key — just the wrong one
        var transport = new FakeTransport();
        var sut = new ClipboardKeyExchange(keys, transport, () => "me-dev");

        await sut.ReportDecryptFailureAsync();
        await sut.ReportDecryptFailureAsync();
        (await sut.ReportDecryptFailureAsync()).Should().BeTrue();

        transport.Upserts.Should().ContainSingle();
        transport.Upserts[0].settings.NeedsTextKey.Should().BeTrue();

        // The streak was consumed: the very next failure starts a fresh count, no instant re-request.
        (await sut.ReportDecryptFailureAsync()).Should().BeFalse();
        transport.Upserts.Should().HaveCount(1);
    }

    [Fact]
    public async Task ReportDecryptSuccess_ResetsTheStreak()
    {
        var transport = new FakeTransport();
        var sut = new ClipboardKeyExchange(new FakeKeyStore(TextCrypto.NewKey()), transport, () => "me-dev");

        await sut.ReportDecryptFailureAsync();
        await sut.ReportDecryptFailureAsync();
        sut.ReportDecryptSuccess();
        await sut.ReportDecryptFailureAsync();
        await sut.ReportDecryptFailureAsync();

        transport.Upserts.Should().BeEmpty(); // never reached 3 consecutive
    }

    // ----- Live triggers: settings broadcast + presence change -----

    [Fact]
    public async Task SettingsBroadcast_PeerAdvertisingNeed_TriggersAnAdmissionSweep()
    {
        using var peerRsa = RSA.Create(2048);
        var peerPub = KeyWrap.ExportPublicKey(peerRsa);
        var transport = new FakeTransport { Devices = new[] { Peer("needy", needsKey: true, publicKey: peerPub) } };
        _ = new ClipboardKeyExchange(new FakeKeyStore(TextCrypto.NewKey()), transport, () => "me-dev");

        transport.RaiseSettings("needy", new ClipboardSettings { NeedsTextKey = true });

        await WaitUntilAsync(() => transport.Relays.Count == 1);
        transport.Relays.Should().ContainSingle();
        transport.Relays[0].target.Should().Be("needy");
    }

    [Fact]
    public async Task SettingsBroadcast_WithoutNeed_DoesNotSweep()
    {
        var transport = new FakeTransport();
        _ = new ClipboardKeyExchange(new FakeKeyStore(TextCrypto.NewKey()), transport, () => "me-dev");

        transport.RaiseSettings("peer", new ClipboardSettings { NeedsTextKey = false });
        transport.RaiseSettings("peer", new ClipboardSettings()); // null = not an advertisement either

        await Task.Delay(150); // give any (wrong) background sweep a chance to surface
        transport.GetDevicesCalls.Should().Be(0);
        transport.Relays.Should().BeEmpty();
    }

    [Fact]
    public async Task PresenceChange_TriggersAnAdmissionSweep_SoAWaitingPeerIsAdmittedWhenItComesOnline()
    {
        using var peerRsa = RSA.Create(2048);
        var transport = new FakeTransport
        {
            Devices = new[] { Peer("needy", needsKey: true, publicKey: KeyWrap.ExportPublicKey(peerRsa)) },
        };
        _ = new ClipboardKeyExchange(new FakeKeyStore(TextCrypto.NewKey()), transport, () => "me-dev");

        transport.RaisePresence("me-dev", "needy");

        await WaitUntilAsync(() => transport.Relays.Count == 1);
        transport.Relays.Should().ContainSingle();
        transport.Relays[0].target.Should().Be("needy");
    }
}
