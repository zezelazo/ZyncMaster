using System.Security.Cryptography;
using FluentAssertions;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests.Clipboard;

public sealed class TextCryptoTests
{
    [Fact]
    public void NewKey_Is32Bytes()
    {
        TextCrypto.NewKey().Length.Should().Be(32);
    }

    [Fact]
    public void NewKey_DiffersEachCall()
    {
        TextCrypto.NewKey().Should().NotEqual(TextCrypto.NewKey());
    }

    [Fact]
    public void EncryptDecrypt_RoundTripsAscii()
    {
        var key = TextCrypto.NewKey();
        const string plaintext = "hello clipboard";

        var blob = TextCrypto.Encrypt(key, plaintext);

        TextCrypto.Decrypt(key, blob).Should().Be(plaintext);
    }

    [Fact]
    public void EncryptDecrypt_RoundTripsUnicode()
    {
        var key = TextCrypto.NewKey();
        const string plaintext = "áéí 日本語 😀";

        var blob = TextCrypto.Encrypt(key, plaintext);

        TextCrypto.Decrypt(key, blob).Should().Be(plaintext);
    }

    [Fact]
    public void EncryptDecrypt_RoundTripsEmpty()
    {
        var key = TextCrypto.NewKey();

        var blob = TextCrypto.Encrypt(key, string.Empty);

        TextCrypto.Decrypt(key, blob).Should().Be(string.Empty);
    }

    [Fact]
    public void Encrypt_CiphertextDoesNotContainPlaintext()
    {
        var key = TextCrypto.NewKey();
        const string plaintext = "secret-marker-1234";

        var blob = TextCrypto.Encrypt(key, plaintext);

        System.Text.Encoding.UTF8.GetString(blob).Should().NotContain(plaintext);
    }

    [Fact]
    public void Encrypt_DiffersPerCall_RandomNonce()
    {
        var key = TextCrypto.NewKey();
        const string plaintext = "same input";

        var a = TextCrypto.Encrypt(key, plaintext);
        var b = TextCrypto.Encrypt(key, plaintext);

        a.Should().NotEqual(b);
    }

    [Fact]
    public void Blob_HasNoncePlusTagOverhead()
    {
        var key = TextCrypto.NewKey();

        var blob = TextCrypto.Encrypt(key, "x");

        // [12 nonce][16 tag][ciphertext]; ciphertext length == plaintext length (1 byte).
        blob.Length.Should().Be(12 + 16 + 1);
    }

    [Fact]
    public void Decrypt_WithWrongKey_Throws()
    {
        var key = TextCrypto.NewKey();
        var wrongKey = TextCrypto.NewKey();
        var blob = TextCrypto.Encrypt(key, "payload");

        var act = () => TextCrypto.Decrypt(wrongKey, blob);

        act.Should().Throw<AuthenticationTagMismatchException>();
    }
}
