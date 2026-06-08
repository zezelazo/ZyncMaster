using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ZyncMaster.Server.Tests;

public sealed class PathBaseTests
{
    [Fact]
    public async Task Health_IsReachable_UnderConfiguredPathBase()
    {
        using var factory = new ServerTestFactory();
        var client = factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["Server:PathBase"] = "/zync",
                }))).CreateClient();

        var underPrefix = await client.GetAsync("/zync/health");
        underPrefix.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
