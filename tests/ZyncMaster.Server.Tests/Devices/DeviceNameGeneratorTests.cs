using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Server.Tests.Devices;

// Pure-unit tests for the device name generator. No IO, no host: the generator takes the account
// identifier and the set of already-taken names and returns a unique, friendly geek name.
public class DeviceNameGeneratorTests
{
    private static readonly string[] Characters =
    {
        "frodo", "gandalf", "aragorn", "legolas", "gimli", "samwise", "boromir",
        "galadriel", "elrond", "eowyn", "faramir", "theoden", "bilbo", "merry",
        "pippin", "treebeard", "arwen", "celeborn", "radagast", "eomer",
        "neo", "trinity", "morpheus", "oracle", "niobe", "switch", "tank",
        "dozer", "cypher", "apoc", "mouse", "link", "sparks", "ghost",
    };

    [Fact]
    public void Generate_produces_character_dash_accountSlug()
    {
        var gen = new DeviceNameGenerator();

        var name = gen.Generate("zezelazo@msn.com", Array.Empty<string>());

        name.Should().Contain("-zezelazo");
        var character = name[..name.IndexOf('-')];
        Characters.Should().Contain(character);
    }

    [Fact]
    public void Generate_uses_email_local_part_as_slug()
    {
        var gen = new DeviceNameGenerator();

        var name = gen.Generate("john.doe@example.com", Array.Empty<string>());

        name.Should().EndWith("-john-doe");
    }

    [Fact]
    public void Generate_slugifies_display_name_with_spaces()
    {
        var gen = new DeviceNameGenerator();

        var name = gen.Generate("Zeze Lazo", Array.Empty<string>());

        name.Should().EndWith("-zeze-lazo");
    }

    [Fact]
    public void Generate_is_deterministic_for_same_input()
    {
        var gen = new DeviceNameGenerator();

        var a = gen.Generate("stable@example.com", Array.Empty<string>());
        var b = gen.Generate("stable@example.com", Array.Empty<string>());

        a.Should().Be(b, "no Random — the character pick is derived from a stable hash of the slug");
    }

    [Fact]
    public void Generate_returns_different_name_when_base_is_taken()
    {
        var gen = new DeviceNameGenerator();

        var first = gen.Generate("zezelazo@msn.com", Array.Empty<string>());
        var second = gen.Generate("zezelazo@msn.com", new[] { first });

        second.Should().NotBe(first);
        second.Should().EndWith("-zezelazo", "the next free character of the pool keeps the same slug");
    }

    [Fact]
    public void Generate_uniqueness_is_case_insensitive()
    {
        var gen = new DeviceNameGenerator();

        var first = gen.Generate("zezelazo@msn.com", Array.Empty<string>());
        var second = gen.Generate("zezelazo@msn.com", new[] { first.ToUpperInvariant() });

        // Even though the taken name was upper-cased, the generator treats it as a collision.
        string.Equals(second, first, StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public void Generate_appends_numeric_suffix_when_whole_pool_exhausted()
    {
        var gen = new DeviceNameGenerator();

        // Take every "{character}-acme" so the generator must fall back to a numeric suffix.
        var taken = Characters.Select(c => $"{c}-acme").ToList();

        var name = gen.Generate("acme@example.com", taken);

        taken.Should().NotContain(name);
        name.Should().MatchRegex(@"^[a-z]+-acme-\d+$");
    }

    [Fact]
    public void Generate_returns_distinct_names_across_many_devices_for_same_account()
    {
        var gen = new DeviceNameGenerator();
        var taken = new List<string>();

        for (var i = 0; i < 50; i++)
            taken.Add(gen.Generate("repeat@example.com", taken));

        taken.Select(n => n.ToLowerInvariant()).Distinct().Should().HaveCount(50);
    }

    [Fact]
    public void Generate_never_exceeds_max_length()
    {
        var gen = new DeviceNameGenerator();
        var longEmail = new string('a', 300) + "@example.com";

        var name = gen.Generate(longEmail, Array.Empty<string>());

        name.Length.Should().BeLessThanOrEqualTo(DeviceNameGenerator.MaxNameLength);
    }

    [Fact]
    public void Generate_stays_within_max_length_even_with_suffix()
    {
        var gen = new DeviceNameGenerator();
        var longEmail = new string('b', 300) + "@example.com";

        var first = gen.Generate(longEmail, Array.Empty<string>());
        var second = gen.Generate(longEmail, new[] { first });
        var withSuffix = gen.Generate(longEmail, Characters.Select(c => $"{c}-{new string('b', 300)}").ToList());

        first.Length.Should().BeLessThanOrEqualTo(DeviceNameGenerator.MaxNameLength);
        second.Length.Should().BeLessThanOrEqualTo(DeviceNameGenerator.MaxNameLength);
        withSuffix.Length.Should().BeLessThanOrEqualTo(DeviceNameGenerator.MaxNameLength);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@@@")]
    [InlineData("!!!@example.com")]
    public void Generate_falls_back_to_device_slug_for_empty_or_unusable_account(string? account)
    {
        var gen = new DeviceNameGenerator();

        var name = gen.Generate(account, Array.Empty<string>());

        name.Should().EndWith("-device");
        name.Length.Should().BeLessThanOrEqualTo(DeviceNameGenerator.MaxNameLength);
    }

    [Fact]
    public void ToSlug_normalizes_accents_to_ascii()
    {
        DeviceNameGenerator.ToSlug("José Ñandú").Should().Be("jose-nandu");
    }

    [Fact]
    public void ToSlug_collapses_separators_and_trims_hyphens()
    {
        DeviceNameGenerator.ToSlug("  Zeze   _-_  Lazo  ").Should().Be("zeze-lazo");
    }

    [Fact]
    public void ToSlug_uses_email_local_part_only()
    {
        DeviceNameGenerator.ToSlug("zeze.lazo@devlabperu.com").Should().Be("zeze-lazo");
    }

    [Fact]
    public void Generate_null_existing_throws()
    {
        var gen = new DeviceNameGenerator();
        Action act = () => gen.Generate("x@example.com", null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
