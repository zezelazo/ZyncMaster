using System.Security.Cryptography;

namespace ZyncMaster.Engine;

// RSA-OAEP (SHA-256) wrapping of the shared symmetric text key, used when a new device joins: the
// existing device wraps the text key with the new device's public key and relays it through the
// server, which never sees the plaintext key. SPKI is the portable public-key format. RSA-OAEP is
// cross-platform on net10.0.
public static class KeyWrap
{
    // The device's public key in SubjectPublicKeyInfo (SPKI) DER form, ready to ship to a peer.
    public static byte[] ExportPublicKey(RSA rsa)
    {
        ArgumentNullException.ThrowIfNull(rsa);
        return rsa.ExportSubjectPublicKeyInfo();
    }

    // Encrypts the text key for the holder of targetPublicKey (SPKI DER).
    public static byte[] Wrap(byte[] targetPublicKey, byte[] textKey)
    {
        ArgumentNullException.ThrowIfNull(targetPublicKey);
        ArgumentNullException.ThrowIfNull(textKey);

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(targetPublicKey, out _);
        return rsa.Encrypt(textKey, RSAEncryptionPadding.OaepSHA256);
    }

    // Recovers the text key using this device's private key.
    public static byte[] Unwrap(RSA myPrivate, byte[] wrapped)
    {
        ArgumentNullException.ThrowIfNull(myPrivate);
        ArgumentNullException.ThrowIfNull(wrapped);
        return myPrivate.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
    }
}
