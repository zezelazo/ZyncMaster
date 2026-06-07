using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

// Orchestrates the E2E text-key lifecycle across devices. The server never sees the plaintext text
// key: the first device generates it; every later device receives it wrapped (RSA-OAEP) against its
// own public key, relayed by an already-admitted peer.
public sealed class ClipboardKeyExchange
{
    private readonly IClipboardKeyStore _keys;
    private readonly IClipboardTransport _transport;

    public ClipboardKeyExchange(IClipboardKeyStore keys, IClipboardTransport transport)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    // Resolves this device's copy of the shared text key:
    //  - already have it locally -> return it (no-op);
    //  - first device (history empty, so no existing text we couldn't read) -> generate + save;
    //  - otherwise -> null: history holds text encrypted with a key we don't have, so we cannot
    //    self-generate. The caller waits for a peer to relay the key via OnKeyReceivedAsync.
    public async Task<byte[]?> EnsureTextKeyAsync(bool historyIsEmpty, CancellationToken ct = default)
    {
        var existing = await _keys.LoadTextKeyAsync(ct).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        if (historyIsEmpty)
        {
            var k = TextCrypto.NewKey();
            await _keys.SaveTextKeyAsync(k, ct).ConfigureAwait(false);
            return k;
        }

        return null;
    }

    // Admits a new device by wrapping our text key against its public key and relaying the blob
    // through the server (server-blind). Requires that we already hold the text key.
    public async Task AdmitDeviceAsync(string targetDeviceId, byte[] targetPublicKey, string myDeviceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targetDeviceId);
        ArgumentNullException.ThrowIfNull(targetPublicKey);
        ArgumentNullException.ThrowIfNull(myDeviceId);

        var key = await _keys.LoadTextKeyAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Cannot admit a device before this device holds the text key.");

        var wrapped = KeyWrap.Wrap(targetPublicKey, key);
        await _transport.RelayKeyAsync(myDeviceId, targetDeviceId, wrapped, ct).ConfigureAwait(false);
    }

    // Handles an inbound relayed key: unwrap with our device private key and persist it as the shared
    // text key, which makes encrypted history readable from here on.
    public async Task OnKeyReceivedAsync(string fromDeviceId, byte[] wrapped, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wrapped);

        var (_, priv) = await _keys.EnsureDeviceKeypairAsync(ct).ConfigureAwait(false);
        var key = KeyWrap.Unwrap(priv, wrapped);
        await _keys.SaveTextKeyAsync(key, ct).ConfigureAwait(false);
    }
}
