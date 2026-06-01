using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Server.Tests.Devices;

public class ApiKeyTests
{
    [Fact]
    public void Generate_produces_url_safe_legacy_key_without_separator()
    {
        // The single-string Generate() returns a LEGACY-format key: url-safe, opaque, and with NO
        // keyId separator (modern keys come from GenerateKey() as "keyId.secret"). The legacy shape
        // routes through the scan-path branch of the api-key handler.
        var key = ApiKeyGenerator.Generate();
        Regex.IsMatch(key, "^[A-Za-z0-9_-]{20,64}$").Should().BeTrue();
        key.Should().NotContain(".");
    }

    [Fact]
    public void GenerateKey_produces_keyId_and_secret()
    {
        var generated = ApiKeyGenerator.GenerateKey();
        generated.ApiKey.Should().Be($"{generated.KeyId}.{generated.Secret}");
        ApiKeyGenerator.TryParse(generated.ApiKey, out var keyId, out var secret).Should().BeTrue();
        keyId.Should().Be(generated.KeyId);
        secret.Should().Be(generated.Secret);
        Regex.IsMatch(generated.KeyId, "^[A-Za-z0-9_-]+$").Should().BeTrue();
        Regex.IsMatch(generated.Secret, "^[A-Za-z0-9_-]+$").Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("noseparator")]
    [InlineData(".onlysecret")]
    [InlineData("onlykeyid.")]
    public void TryParse_rejects_malformed_or_legacy_keys(string key)
    {
        ApiKeyGenerator.TryParse(key, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Generate_produces_unique_keys()
    {
        var keys = Enumerable.Range(0, 100).Select(_ => ApiKeyGenerator.Generate()).ToList();
        keys.Distinct().Should().HaveCount(100);
    }

    [Fact]
    public void Hash_then_Verify_succeeds_for_same_key()
    {
        var key = ApiKeyGenerator.Generate();
        var stored = ApiKeyHasher.Hash(key);
        ApiKeyHasher.Verify(key, stored).Should().BeTrue();
    }

    [Fact]
    public void Verify_fails_for_wrong_key()
    {
        var stored = ApiKeyHasher.Hash(ApiKeyGenerator.Generate());
        ApiKeyHasher.Verify(ApiKeyGenerator.Generate(), stored).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("v2.100000.aaaa.bbbb")]
    [InlineData("v1.notanumber.aaaa.bbbb")]
    [InlineData("v1.100000.onlythreeparts")]
    public void Verify_fails_for_tampered_or_garbage_stored(string stored)
    {
        ApiKeyHasher.Verify("anykey", stored).Should().BeFalse();
    }

    [Fact]
    public void Same_key_hashed_twice_yields_different_stored_that_both_verify()
    {
        var key = ApiKeyGenerator.Generate();
        var a = ApiKeyHasher.Hash(key);
        var b = ApiKeyHasher.Hash(key);
        a.Should().NotBe(b);
        ApiKeyHasher.Verify(key, a).Should().BeTrue();
        ApiKeyHasher.Verify(key, b).Should().BeTrue();
    }
}
