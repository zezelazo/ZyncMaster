using System.Security.Cryptography;
using System.Text;

namespace ZyncMaster.Engine;

// AES-256-GCM authenticated encryption for clipboard text. The wire format is a single byte blob:
//   [12 bytes nonce][16 bytes tag][ciphertext]
// A fresh random 96-bit nonce is generated per call, so encrypting the same plaintext twice yields
// different blobs. Decryption verifies the tag — a wrong key (or tampered blob) throws
// AuthenticationTagMismatchException. AES-GCM is cross-platform on net10.0.
public static class TextCrypto
{
    private const int KeySizeBytes = 32;   // AES-256
    private const int NonceSizeBytes = 12;  // 96-bit GCM nonce
    private const int TagSizeBytes = 16;    // 128-bit GCM tag

    // 32 cryptographically random bytes for use as the shared symmetric key.
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySizeBytes);

    public static byte[] Encrypt(byte[] key, string plaintext)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(plaintext);

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var tag = new byte[TagSizeBytes];
        var cipher = new byte[plainBytes.Length];

        using var gcm = new AesGcm(key, TagSizeBytes);
        gcm.Encrypt(nonce, plainBytes, cipher, tag);

        var blob = new byte[NonceSizeBytes + TagSizeBytes + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSizeBytes);
        Buffer.BlockCopy(tag, 0, blob, NonceSizeBytes, TagSizeBytes);
        Buffer.BlockCopy(cipher, 0, blob, NonceSizeBytes + TagSizeBytes, cipher.Length);
        return blob;
    }

    public static string Decrypt(byte[] key, byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length < NonceSizeBytes + TagSizeBytes)
            throw new ArgumentException("Blob is too short to contain a nonce and tag.", nameof(blob));

        var nonce = new byte[NonceSizeBytes];
        var tag = new byte[TagSizeBytes];
        var cipher = new byte[blob.Length - NonceSizeBytes - TagSizeBytes];
        Buffer.BlockCopy(blob, 0, nonce, 0, NonceSizeBytes);
        Buffer.BlockCopy(blob, NonceSizeBytes, tag, 0, TagSizeBytes);
        Buffer.BlockCopy(blob, NonceSizeBytes + TagSizeBytes, cipher, 0, cipher.Length);

        var plainBytes = new byte[cipher.Length];
        using var gcm = new AesGcm(key, TagSizeBytes);
        gcm.Decrypt(nonce, cipher, tag, plainBytes);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
