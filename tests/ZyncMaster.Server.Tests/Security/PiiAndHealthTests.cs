using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Server.Infrastructure.Email;
using Xunit;

namespace ZyncMaster.Server.Tests.Security;

// FIX 5 — the magic-link email recipient is PII and must be MASKED in logs (LoggingEmailSender), and
// /health must verify the DB and report it (returning 200 + db=up when the DB is reachable).
public sealed class PiiAndHealthTests
{
    [Theory]
    [InlineData("alice@example.com", "a***@example.com")]
    [InlineData("b@d.io", "b***@d.io")]
    [InlineData("Z.User+tag@corp.co.uk", "Z***@corp.co.uk")]
    public void MaskEmail_keeps_first_char_and_domain(string input, string expected)
    {
        LoggingEmailSender.MaskEmail(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("@nolocal.com")]
    [InlineData("trailingat@")]
    public void MaskEmail_fully_masks_blank_or_malformed(string? input)
    {
        LoggingEmailSender.MaskEmail(input).Should().Be("***");
    }

    [Fact]
    public void MaskEmail_never_contains_the_full_local_part()
    {
        // The mask must not leak the rest of the local-part beyond the first character.
        LoggingEmailSender.MaskEmail("sensitive@example.com").Should().NotContain("sensitive");
    }

    [Fact]
    public async Task Health_endpoint_reports_db_up_when_reachable()
    {
        using var factory = new ServerTestFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("ok");
        doc.RootElement.GetProperty("db").GetString().Should().Be("up",
            "the health check confirms the database connection, not just liveness");
    }
}
