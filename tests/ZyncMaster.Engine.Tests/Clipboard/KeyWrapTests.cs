using System.Security.Cryptography;
using FluentAssertions;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests.Clipboard;

public sealed class KeyWrapTests
{
    [Fact]
    public void WrapUnwrap_RoundTripsTextKey()
    {
        using var rsa = RSA.Create(3072);
        var publicKey = KeyWrap.ExportPublicKey(rsa);
        var textKey = TextCrypto.NewKey();

        var wrapped = KeyWrap.Wrap(publicKey, textKey);
        var unwrapped = KeyWrap.Unwrap(rsa, wrapped);

        unwrapped.Should().Equal(textKey);
    }

    [Fact]
    public void Wrap_DiffersFromRawKey()
    {
        using var rsa = RSA.Create(3072);
        var publicKey = KeyWrap.ExportPublicKey(rsa);
        var textKey = TextCrypto.NewKey();

        var wrapped = KeyWrap.Wrap(publicKey, textKey);

        wrapped.Should().NotEqual(textKey);
        wrapped.Length.Should().BeGreaterThan(textKey.Length);
    }

    [Fact]
    public void ExportPublicKey_ProducesImportableSpki()
    {
        using var rsa = RSA.Create(3072);

        var publicKey = KeyWrap.ExportPublicKey(rsa);

        using var imported = RSA.Create();
        var act = () => imported.ImportSubjectPublicKeyInfo(publicKey, out _);
        act.Should().NotThrow();
    }

    [Fact]
    public void Unwrap_WithWrongPrivateKey_Throws()
    {
        using var rsa = RSA.Create(3072);
        using var other = RSA.Create(3072);
        var wrapped = KeyWrap.Wrap(KeyWrap.ExportPublicKey(rsa), TextCrypto.NewKey());

        var act = () => KeyWrap.Unwrap(other, wrapped);

        act.Should().Throw<CryptographicException>();
    }
}
