using System;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Core.Tests;

public sealed class UuidV5Tests
{
    // RFC 4122 §C.1 namespace for DNS — used to validate the implementation
    // against published reference vectors.
    private static readonly Guid DnsNamespace = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

    [Fact]
    public void Create_DnsNamespace_PythonExample_MatchesKnownValue()
    {
        // Reference value from Python: uuid.uuid5(uuid.NAMESPACE_DNS, "python.org")
        var result = UuidV5.Create(DnsNamespace, "python.org");

        result.ToString().Should().Be("886313e1-3b8a-5372-9b90-0c9aee199e5d");
    }

    [Fact]
    public void Create_DnsNamespace_WwwExampleCom_MatchesKnownValue()
    {
        // Reference value from Python: uuid.uuid5(uuid.NAMESPACE_DNS, "www.example.com")
        var result = UuidV5.Create(DnsNamespace, "www.example.com");

        result.ToString().Should().Be("2ed6657d-e927-568b-95e1-2665a8aea6a2");
    }

    [Fact]
    public void Create_SameInputs_ProducesSameGuid()
    {
        var a = UuidV5.Create(DnsNamespace, "anything");
        var b = UuidV5.Create(DnsNamespace, "anything");

        a.Should().Be(b);
    }

    [Fact]
    public void Create_DifferentNames_ProducesDifferentGuids()
    {
        var a = UuidV5.Create(DnsNamespace, "name1");
        var b = UuidV5.Create(DnsNamespace, "name2");

        a.Should().NotBe(b);
    }

    [Fact]
    public void Create_DifferentNamespaces_ProducesDifferentGuids()
    {
        var ns1 = new Guid("11111111-1111-1111-1111-111111111111");
        var ns2 = new Guid("22222222-2222-2222-2222-222222222222");

        UuidV5.Create(ns1, "x").Should().NotBe(UuidV5.Create(ns2, "x"));
    }

    [Fact]
    public void Create_VersionBitsAre5()
    {
        var guid  = UuidV5.Create(DnsNamespace, "test");
        var bytes = guid.ToByteArray();

        // After Microsoft's GUID layout swap, byte 7 in the array holds RFC byte 6.
        // The upper nibble must be 0x5 for version 5.
        (bytes[7] & 0xF0).Should().Be(0x50);
    }

    [Fact]
    public void Create_VariantBitsAreRfc4122()
    {
        var guid  = UuidV5.Create(DnsNamespace, "test");
        var bytes = guid.ToByteArray();

        // Byte 8 upper bits must be 10xxxxxx.
        (bytes[8] & 0xC0).Should().Be(0x80);
    }

    [Fact]
    public void Create_NullName_Throws()
    {
        Action act = () => UuidV5.Create(DnsNamespace, null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
