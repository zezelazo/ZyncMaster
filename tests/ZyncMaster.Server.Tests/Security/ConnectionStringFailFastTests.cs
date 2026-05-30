using System;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ZyncMaster.Server.Tests.Security;

// Program.cs fail-fast: in any non-Development environment a missing
// ConnectionStrings:ZyncMasterDb must abort host build with a clear error rather than
// silently falling through to the Development LocalDB default. This guards against a prod
// deployment quietly pointing at a non-existent local database.
public class ConnectionStringFailFastTests
{
    private sealed class ProductionNoConnStringFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");

            // Strip any connection string that ambient config (env vars / user secrets) might
            // supply so the fail-fast branch is the one exercised. We do NOT register the
            // SQLite test provider here: this test asserts the config guard, not real Migrate.
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["ConnectionStrings:ZyncMasterDb"] = null,
                });
            });
        }
    }

    [Fact]
    public void Production_without_connection_string_fails_host_build()
    {
        using var factory = new ProductionNoConnStringFactory();

        // Host build is lazy; touching Services forces Program's top-level statements to run,
        // including the connection-string guard, which throws before the app is built.
        var act = () => _ = factory.Services;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:ZyncMasterDb*");
    }
}
