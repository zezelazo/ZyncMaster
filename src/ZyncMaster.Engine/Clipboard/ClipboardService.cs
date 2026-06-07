using System.Threading;
using System.Threading.Tasks;

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
    private readonly IClipboardCaptureSource _capture;
    private readonly IClipboardTransport _transport;
    private readonly IClipboardSink _sink;
    private readonly IClipboardKeyStore _keys;
    private readonly ClipboardKeyExchange _keyExchange;
    private readonly ClipboardDedupe _dedupe;
    private readonly Func<ClipboardSettings> _settings;
    private readonly long _hardMaxImageBytes;

    public ClipboardService(
        IClipboardCaptureSource capture,
        IClipboardTransport transport,
        IClipboardSink sink,
        IClipboardKeyStore keys,
        ClipboardKeyExchange keyExchange,
        ClipboardDedupe dedupe,
        Func<ClipboardSettings> settings,
        long hardMaxImageBytes)
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

        _capture.Captured += OnCaptured;
        _transport.ItemReceived += OnReceived;
        _transport.KeyReceived += OnKeyReceived;
    }

    public void Start() => _capture.Start();
    public void Stop() => _capture.Stop();

    private async void OnCaptured(ClipboardEntry entry) =>
        await PublishCapturedAsync(entry, CancellationToken.None).ConfigureAwait(false);

    private async void OnReceived(ClipboardEntry entry) =>
        await ApplyReceivedAsync(entry, CancellationToken.None).ConfigureAwait(false);

    private async void OnKeyReceived(string fromDeviceId, byte[] wrapped) =>
        await _keyExchange.OnKeyReceivedAsync(fromDeviceId, wrapped, CancellationToken.None).ConfigureAwait(false);

    private async Task PublishCapturedAsync(ClipboardEntry entry, CancellationToken ct)
    {
        var settings = _settings();
        if (!settings.Send)
            return;

        var hash = _dedupe.Hash(entry);
        if (_dedupe.IsEcho(hash))
            return; // our own just-applied content bouncing back from the OS — drop it.

        if (entry.Type == ClipboardEntryType.Text)
        {
            // Encrypt with the shared text key; if we don't have it yet we cannot publish text
            // without leaking plaintext, so we stop and wait for key admission.
            var key = await _keys.LoadTextKeyAsync(ct).ConfigureAwait(false);
            if (key is null)
                return;

            var cipher = TextCrypto.Encrypt(key, entry.Text!);
            await _transport.PublishAsync(entry with { Text = null, CipherText = cipher }, ct).ConfigureAwait(false);
            return;
        }

        // Image: enforce the hard cap on the real bytes; never throw on oversize.
        var size = entry.ImageBytes?.Length ?? 0;
        if (size > _hardMaxImageBytes)
            return;

        await _transport.PublishAsync(entry with { SizeBytes = size }, ct).ConfigureAwait(false);
    }

    private async Task ApplyReceivedAsync(ClipboardEntry entry, CancellationToken ct)
    {
        var settings = _settings();
        if (!settings.Receive)
            return;

        if (entry.Type == ClipboardEntryType.Text)
        {
            var key = await _keys.LoadTextKeyAsync(ct).ConfigureAwait(false);
            if (key is null)
                return; // cannot decrypt yet — usable after key admission.

            var plain = TextCrypto.Decrypt(key, entry.CipherText!);
            entry = entry with { Text = plain };
        }

        if (!settings.AutoSync)
            return;

        // Mark BEFORE applying so the OS echo capture is recognized and not re-published.
        _dedupe.MarkApplied(_dedupe.Hash(entry));
        await _sink.SetAsync(entry, ct).ConfigureAwait(false);
    }
}
