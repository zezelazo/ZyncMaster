using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace SyncMaster.Server.Tests.Devices;

public class ApiKeyTests
{
    [Fact]
    public void Generate_produces_url_safe_key_of_expected_shape()
    {
        var key = ApiKeyGenerator.Generate();
        Regex.IsMatch(key, "^[A-Za-z0-9_-]{43}$").Should().BeTrue();
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
