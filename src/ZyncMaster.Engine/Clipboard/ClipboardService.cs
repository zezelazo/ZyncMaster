using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.Core;

namespace ZyncMaster.Engine;

// The device-side clipboard orchestrator. Wires the OS capture source and the live transport, and on
// each event applies the send/receive/auto-sync gates, E2E text encryption/decryption (the server is
// blind to plaintext), the image hard-cap, and echo suppression so an applied item is never bounced
// back to its sender.
//
// Boundary contract for text: outbound, Text is the local plaintext and CipherText carries
// TextCrypto.Encrypt(key, Text); we publish with Text stripped so no plaintext leaves the device.
// Inbound, the transport delivers CipherText with Text null; we decrypt into Text before applying.
public sealed class ClipboardService
{
    // Smallest image capture worth publishing. A bare CF_DIB header without pixel data is 40-124
    // bytes; no real picture is under 256.
    internal const int MinPublishImageBytes = 256;

    private readonly IClipboardCaptureSource _capture;
    private readonly IClipboardTransport _transport;
    private readonly IClipboardSink _sink;
    private readonly IClipboardKeyStore _keys;
    private readonly ClipboardKeyExchange _keyExchange;
    private readonly ClipboardDedupe _dedupe;
    private readonly Func<ClipboardSettings> _settings;
    private readonly long _hardMaxImageBytes;
    private readonly IAppLogger _logger;

    public ClipboardService(
        IClipboardCaptureSource capture,
        IClipboardTransport transport,
        IClipboardSink sink,
        IClipboardKeyStore keys,
        ClipboardKeyExchange keyExchange,
        ClipboardDedupe dedupe,
        Func<ClipboardSettings> settings,
        long hardMaxImageBytes,
        IAppLogger logger)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _keyExchange = keyExchange ?? throw new ArgumentNullException(nameof(keyExchange));
        _dedupe = dedupe ?? throw new ArgumentNullException(nameof(dedupe));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (hardMaxImageBytes < 0) throw new ArgumentOutOfRangeException(nameof(hardMaxImageBytes));
        _hardMaxImageBytes = hardMaxImageBytes;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _capture.Captured += OnCaptured;
        _transport.ItemReceived += OnReceived;
        _transport.KeyReceived += OnKeyReceived;
    }

    public void Start() => _capture.Start();
    public void Stop() => _capture.Stop();

    // Pastes a history item (already resolved to plaintext for Text) into the focused window. Marks the
    // dedupe with the content hash BEFORE the OS write — mirroring ApplyReceivedAsync — so the
    // WM_CLIPBOARDUPDATE the programmatic write triggers is recognized as our own echo in
    // PublishCapturedAsync and dropped, rather than being re-encrypted and re-published as a new copy
    // (the echo loop the dedupe exists to prevent). Returns whatever the sink reports: false when there
    // was nothing to write (so the caller does not report a no-op as a successful paste).
    public Task<bool> PasteAsync(ClipboardEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _dedupe.MarkApplied(_dedupe.Hash(entry));
        return _sink.PasteIntoFocusedAsync(entry, ct);
    }

    // These three are the async-void event boundaries: ANY exception that escapes them terminates
    // the whole process (that is how the nginx-413-on-image crash killed the App before the publish
    // path caught transport errors). The inner methods log their own failures with precise reasons;
    // the catch-alls here are the last line of defense for whatever they did not anticipate — e.g. a
    // malformed relayed key blob making KeyWrap.Unwrap throw CryptographicException.
    private async void OnCaptured(ClipboardEntry entry)
    {
        try { await PublishCapturedAsync(entry, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.Log(LogLevel.Warning, "Clipboard capture handling failed; the item was not synced.", ex); }
    }

    private async void OnReceived(ClipboardEntry entry)
    {
        try { await ApplyReceivedAsync(entry, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.Log(LogLevel.Warning, "Inbound clipboard item could not be applied.", ex); }
    }

    private async void OnKeyReceived(string fromDeviceId, byte[] wrapped)
    {
        try { await _keyExchange.OnKeyReceivedAsync(fromDeviceId, wrapped, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.Log(LogLevel.Warning, $"Relayed clipboard key from device '{fromDeviceId}' could not be processed; waiting for the next relay.", ex); }
    }

    // Every drop on this path is logged with its reason: a user watching the log must be able to
    // tell WHY a copy never reached the other devices. User-intended gates (send off) and echo
    // suppression are Debug noise; everything else — missing key, oversize image, transport
    // failure — is a Warning. Transport failures are caught here (the caller is async void, so a
    // rethrow would vanish or crash the process — e.g. nginx 413 on a large image).
    private async Task PublishCapturedAsync(ClipboardEntry entry, CancellationToken ct)
    {
        var settings = _settings();
        if (!settings.Send)
        {
            _logger.Log(LogLevel.Debug, "Clipboard capture dropped: sending is turned off for this device.");
            return;
        }

        var hash = _dedupe.Hash(entry);
        if (_dedupe.IsEcho(hash))
        {
            // Our own just-applied content bouncing back from the OS — expected, not a failure.
            _logger.Log(LogLevel.Debug, "Clipboard capture dropped: echo of our own just-applied content.");
            return;
        }

        if (_dedupe.IsRecentlyPublished(hash))
        {
            // The same content was published moments ago: Windows fires WM_CLIPBOARDUPDATE 2-3
            // times for a single copy (more under RDP clipboard redirection), and users re-copy the
            // same content in bursts. Re-publishing would duplicate history and feed echo loops.
            _logger.Log(LogLevel.Debug, "Clipboard capture dropped: identical content was already published moments ago.");
            return;
        }

        if (entry.Type == ClipboardEntryType.Text)
        {
            // Encrypt with the shared text key; if we don't have it yet we cannot publish text
            // without leaking plaintext, so we stop and wait for key admission.
            var key = await _keys.LoadTextKeyAsync(ct).ConfigureAwait(false);
            if (key is null)
            {
                _logger.Log(LogLevel.Warning,
                    "Clipboard text not synced: the text key has not been admitted to this device yet.");
                return;
            }

            var cipher = TextCrypto.Encrypt(key, entry.Text!);
            try
            {
                await _transport.PublishAsync(entry with { Text = null, CipherText = cipher }, ct).ConfigureAwait(false);
                _dedupe.MarkPublished(hash);
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Warning, $"Clipboard text publish failed: {ex.Message}", ex);
            }
            return;
        }

        // Image: a capture below the minimum is a bare clipboard header, not a picture — e.g. a
        // 76-byte CF_DIB with no pixel data — and would only spam the history with garbage.
        var size = entry.ImageBytes?.Length ?? 0;
        if (size < MinPublishImageBytes)
        {
            _logger.Log(LogLevel.Debug,
                $"Clipboard image dropped: {size} bytes is below the {MinPublishImageBytes}-byte minimum (bare header, not an image).");
            return;
        }

        // Enforce the hard cap on the real bytes; never throw on oversize.
        if (size > _hardMaxImageBytes)
        {
            _logger.Log(LogLevel.Warning,
                $"Clipboard image dropped: {size} bytes exceeds the {_hardMaxImageBytes}-byte cap.");
            return;
        }

        try
        {
            await _transport.PublishAsync(entry with { SizeBytes = size }, ct).ConfigureAwait(false);
            _dedupe.MarkPublished(hash);
        }
        catch (Exception ex)
        {
            // A 413 from a reverse proxy on a big image lands here, among others.
            _logger.Log(LogLevel.Warning, $"Clipboard image publish failed: {ex.Message}", ex);
        }
    }

    // Mirror of the publish path: every dropped inbound item is logged with its reason so a device
    // that "never receives anything" can be diagnosed from its own log.
    private async Task ApplyReceivedAsync(ClipboardEntry entry, CancellationToken ct)
    {
        var settings = _settings();
        if (!settings.Receive)
        {
            _logger.Log(LogLevel.Debug, "Clipboard item dropped: receiving is turned off for this device.");
            return;
        }

        if (entry.Type == ClipboardEntryType.Text)
        {
            var key = await _keys.LoadTextKeyAsync(ct).ConfigureAwait(false);
            if (key is null)
            {
                _logger.Log(LogLevel.Warning,
                    "Received clipboard text cannot be decrypted yet: waiting for the text key to be admitted.");
                return;
            }

            string plain;
            try
            {
                plain = TextCrypto.Decrypt(key, entry.CipherText!);
            }
            catch (CryptographicException)
            {
                // Wrong key (e.g. both sides self-generated): report it. After enough consecutive
                // failures the key exchange marks our key suspect and re-requests a fresh copy from
                // the peers; the relayed key overwrites ours and both sides converge.
                _logger.Log(LogLevel.Warning,
                    "Received clipboard text could not be decrypted with the current text key.");
                await _keyExchange.ReportDecryptFailureAsync(ct).ConfigureAwait(false);
                return;
            }

            _keyExchange.ReportDecryptSuccess();
            entry = entry with { Text = plain };
        }

        if (!settings.AutoSync)
        {
            _logger.Log(LogLevel.Debug,
                "Clipboard item not applied: auto-sync is off; it stays available in the viewer.");
            return;
        }

        // Mark BEFORE applying so the OS echo capture is recognized and not re-published.
        _dedupe.MarkApplied(_dedupe.Hash(entry));
        try
        {
            await _sink.SetAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The caller is async void; an OS clipboard write failure must surface in the log.
            _logger.Log(LogLevel.Warning, $"Clipboard apply failed: {ex.Message}", ex);
        }
    }
}
