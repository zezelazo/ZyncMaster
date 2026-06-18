using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.Core;

namespace ZyncMaster.Engine;

// Orchestrates the E2E text-key lifecycle across devices. The server never sees the plaintext text
// key: the first device generates it; every later device receives it wrapped (RSA-OAEP) against its
// own public key, relayed by an already-admitted peer.
//
// Admission is zero-touch: a device that cannot self-generate publishes "I need the key" plus its
// RSA public key through the per-device settings upsert (RequestKeyAsync); any device that HOLDS
// the key sweeps the roster and relays the wrapped key to every advertised peer
// (AdmitPendingPeersAsync). The sweep re-runs automatically when a settings broadcast shows a peer
// in need and when the presence roster changes (a needy peer just came online), so the two sides
// converge without any user action.
public sealed class ClipboardKeyExchange
{
    // Consecutive text-decrypt failures after which the local key is treated as suspect (wrong key:
    // e.g. both sides self-generated) and a fresh copy is re-requested from the peers.
    private const int DecryptFailureThreshold = 3;

    private readonly IClipboardKeyStore _keys;
    private readonly IClipboardTransport _transport;
    private readonly Func<string?>? _deviceIdProvider;
    private readonly IAppLogger? _logger;

    private readonly object _gate = new();
    // Peers already relayed the key this session — prevents re-spamming the same device on every
    // trigger. A non-delivered relay (peer offline) is NOT recorded, so it retries on the next sweep.
    private readonly HashSet<string> _admittedThisSession = new(StringComparer.Ordinal);
    private int _consecutiveDecryptFailures;

    // Raised after a relayed key was unwrapped and saved (OnKeyReceivedAsync): previously
    // undecryptable history items just became readable, so the host should refresh any open list.
    public event Action? TextKeyChanged;

    // deviceIdProvider resolves THIS device's id lazily (it is unknown until the host registers and
    // initializes the clipboard); while it returns null/empty the orchestration no-ops. logger is
    // optional — per-peer admission failures are logged and tolerated.
    public ClipboardKeyExchange(
        IClipboardKeyStore keys,
        IClipboardTransport transport,
        Func<string?>? deviceIdProvider = null,
        IAppLogger? logger = null)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _deviceIdProvider = deviceIdProvider;
        _logger = logger;

        // Zero-touch triggers: a peer just advertised it needs the key (settings broadcast), or the
        // online roster changed (a needy peer may have just connected, making the relay deliverable).
        // Both sweeps no-op instantly when this device does not hold the key.
        _transport.SettingsChanged += OnPeerSettingsChanged;
        _transport.PresenceChanged += OnPresenceChanged;
    }

    // Resolves this device's copy of the shared text key:
    //  - already have it locally -> return it (no-op);
    //  - no existing TEXT to decrypt (noPriorText) -> this is effectively the first device for text,
    //    so generate + save. NOTE this is gated on the absence of *text*, NOT of all history: images
    //    (and files) never use the text key, so a device whose history holds only images must still
    //    self-generate — otherwise it forever believes a peer must relay a key that never comes, and
    //    every text copy is dropped on a single-device account.
    //  - otherwise -> null: history holds text encrypted with a key we don't have, so we cannot
    //    self-generate. The caller follows up with RequestKeyAsync and waits for a peer to relay
    //    the key via OnKeyReceivedAsync.
    public async Task<byte[]?> EnsureTextKeyAsync(bool noPriorText, CancellationToken ct = default)
    {
        var existing = await _keys.LoadTextKeyAsync(ct).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        if (noPriorText)
        {
            var k = TextCrypto.NewKey();
            await _keys.SaveTextKeyAsync(k, ct).ConfigureAwait(false);
            return k;
        }

        return null;
    }

    // Publishes this device's need for the text key: upserts our per-device settings with
    // needsTextKey=true plus our RSA public key (base64 SPKI), preserving the stored preferences
    // (read-modify-write). A key-holder sees the advertisement — via the settings broadcast or its
    // own roster sweep — wraps the key against the published public key and relays it back.
    public async Task RequestKeyAsync(CancellationToken ct = default)
    {
        var myDeviceId = ResolveDeviceId();
        if (string.IsNullOrEmpty(myDeviceId))
            return; // not registered/initialized yet — nothing to advertise under.

        var (publicKey, _) = await _keys.EnsureDeviceKeypairAsync(ct).ConfigureAwait(false);
        var current = await _transport.GetSettingsAsync(myDeviceId, ct).ConfigureAwait(false);

        await _transport.UpdateSettingsAsync(myDeviceId, current with
        {
            PublicKeyBase64 = Convert.ToBase64String(publicKey),
            NeedsTextKey = true,
        }, ct).ConfigureAwait(false);
    }

    // Key-holder sweep: fetch the roster and relay the wrapped text key to every peer that
    // advertises needsTextKey with a published public key. Each peer is admitted at most once per
    // session (only a DELIVERED relay is recorded, so an offline peer retries on the next trigger);
    // a failure on one peer never blocks the others. Returns the number of keys delivered. No-ops
    // (0) when this device does not hold the key or is not initialized yet.
    public async Task<int> AdmitPendingPeersAsync(CancellationToken ct = default)
    {
        var myDeviceId = ResolveDeviceId();
        if (string.IsNullOrEmpty(myDeviceId))
            return 0;

        if (await _keys.LoadTextKeyAsync(ct).ConfigureAwait(false) is null)
            return 0; // we are a needy device, not a holder.

        var devices = await _transport.GetDevicesAsync(ct).ConfigureAwait(false);

        var admitted = 0;
        foreach (var peer in devices)
        {
            if (string.Equals(peer.DeviceId, myDeviceId, StringComparison.Ordinal))
                continue;
            if (!peer.NeedsTextKey || string.IsNullOrEmpty(peer.PublicKeyBase64))
                continue;

            lock (_gate)
            {
                if (_admittedThisSession.Contains(peer.DeviceId))
                    continue;
            }

            try
            {
                var peerPublicKey = Convert.FromBase64String(peer.PublicKeyBase64);
                var delivered = await AdmitDeviceAsync(peer.DeviceId, peerPublicKey, myDeviceId, ct)
                    .ConfigureAwait(false);

                if (delivered)
                {
                    lock (_gate)
                        _admittedThisSession.Add(peer.DeviceId);
                    admitted++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // shutdown — stop the sweep, do not swallow.
            }
            catch (Exception ex)
            {
                // Per-peer tolerance: a malformed public key or a transient relay failure must not
                // block admission of the remaining peers. Not recorded as admitted -> retried later.
                _logger?.Log(LogLevel.Warning,
                    $"Clipboard key admission failed for device '{peer.DeviceId}'; will retry on the next trigger.", ex);
            }
        }

        return admitted;
    }

    // Admits a new device by wrapping our text key against its public key and relaying the blob
    // through the server (server-blind). Requires that we already hold the text key. Returns the
    // server's delivery verdict: false means the target device was offline and nothing was queued.
    public async Task<bool> AdmitDeviceAsync(string targetDeviceId, byte[] targetPublicKey, string myDeviceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targetDeviceId);
        ArgumentNullException.ThrowIfNull(targetPublicKey);
        ArgumentNullException.ThrowIfNull(myDeviceId);

        var key = await _keys.LoadTextKeyAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Cannot admit a device before this device holds the text key.");

        var wrapped = KeyWrap.Wrap(targetPublicKey, key);
        return await _transport.RelayKeyAsync(myDeviceId, targetDeviceId, wrapped, ct).ConfigureAwait(false);
    }

    // Handles an inbound relayed key: unwrap with our device private key and persist it as the
    // shared text key, which makes encrypted history readable from here on. Then clears our
    // advertised need (best-effort — the key is already saved, so a failed flag-clear only costs a
    // harmless re-admission later) and raises TextKeyChanged so the host refreshes open lists.
    public async Task OnKeyReceivedAsync(string fromDeviceId, byte[] wrapped, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wrapped);

        var (_, priv) = await _keys.EnsureDeviceKeypairAsync(ct).ConfigureAwait(false);
        var key = KeyWrap.Unwrap(priv, wrapped);
        await _keys.SaveTextKeyAsync(key, ct).ConfigureAwait(false);

        // A fresh key invalidates any suspect-key streak accumulated under the old one.
        Interlocked.Exchange(ref _consecutiveDecryptFailures, 0);

        var myDeviceId = ResolveDeviceId();
        if (!string.IsNullOrEmpty(myDeviceId))
        {
            try
            {
                var current = await _transport.GetSettingsAsync(myDeviceId, ct).ConfigureAwait(false);
                await _transport.UpdateSettingsAsync(myDeviceId, current with { NeedsTextKey = false }, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Log(LogLevel.Warning,
                    "Clipboard key received but the needs-key flag could not be cleared; a peer may relay it again (harmless).", ex);
            }
        }

        TextKeyChanged?.Invoke();
    }

    // A text payload decrypted cleanly: the local key works, reset the suspect streak.
    public void ReportDecryptSuccess() => Interlocked.Exchange(ref _consecutiveDecryptFailures, 0);

    // A text payload failed to decrypt (CryptographicException at the caller). After
    // DecryptFailureThreshold consecutive failures the local key is suspect — e.g. two devices each
    // self-generated — so re-advertise the need: a relayed key overwrites ours via SaveTextKeyAsync
    // and both sides converge. Returns true when the re-request was triggered. Never throws: the
    // re-request is best-effort and the caller is a receive path that must keep running.
    public async Task<bool> ReportDecryptFailureAsync(CancellationToken ct = default)
    {
        if (Interlocked.Increment(ref _consecutiveDecryptFailures) < DecryptFailureThreshold)
            return false;

        Interlocked.Exchange(ref _consecutiveDecryptFailures, 0);
        try
        {
            await RequestKeyAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Log(LogLevel.Warning,
                "Clipboard key re-request after repeated decrypt failures failed; will retry after the next failures.", ex);
        }
        return true;
    }

    private string? ResolveDeviceId() => _deviceIdProvider?.Invoke();

    private void OnPeerSettingsChanged(string deviceId, ClipboardSettings settings)
    {
        // Only a peer advertising need is a trigger; its needsTextKey=false clear (or any plain
        // preferences change) must not cause a roster fetch.
        if (settings?.NeedsTextKey == true)
            KickAdmissionSweep();
    }

    private void OnPresenceChanged(IReadOnlyList<string> onlineDeviceIds) => KickAdmissionSweep();

    // Fire-and-forget sweep off the transport's receive thread. AdmitPendingPeersAsync already
    // tolerates per-peer failures; this guard covers the roster fetch itself.
    private void KickAdmissionSweep() =>
        _ = Task.Run(async () =>
        {
            try
            {
                await AdmitPendingPeersAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Log(LogLevel.Warning, "Clipboard key admission sweep failed; will retry on the next trigger.", ex);
            }
        });
}
