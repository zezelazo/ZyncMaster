using System;
using System.Security.Cryptography;
using System.Text;

namespace ZyncMaster.Core;

// RFC 4122 §4.3 — name-based UUID using SHA-1.
// Produces the same UUID for the same (namespace, name) pair on every machine.
public static class UuidV5
{
    public static Guid Create(Guid namespaceId, string name)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));

        var namespaceBytes = namespaceId.ToByteArray();
        SwapByteOrder(namespaceBytes);

        var nameBytes = Encoding.UTF8.GetBytes(name);

        byte[] hash;
        using (var sha1 = SHA1.Create())
        {
            sha1.TransformBlock(namespaceBytes, 0, namespaceBytes.Length, null, 0);
            sha1.TransformFinalBlock(nameBytes, 0, nameBytes.Length);
            hash = sha1.Hash!;
        }

        var result = new byte[16];
        Array.Copy(hash, 0, result, 0, 16);

        // version (5) in upper nibble of byte 6
        result[6] = (byte)((result[6] & 0x0F) | 0x50);
        // variant (RFC 4122) in upper bits of byte 8
        result[8] = (byte)((result[8] & 0x3F) | 0x80);

        SwapByteOrder(result);
        return new Guid(result);
    }

    // Microsoft Guid stores Data1-3 in little-endian; RFC 4122 expects big-endian.
    private static void SwapByteOrder(byte[] guid)
    {
        SwapBytes(guid, 0, 3);
        SwapBytes(guid, 1, 2);
        SwapBytes(guid, 4, 5);
        SwapBytes(guid, 6, 7);
    }

    private static void SwapBytes(byte[] guid, int left, int right)
        => (guid[left], guid[right]) = (guid[right], guid[left]);
}
