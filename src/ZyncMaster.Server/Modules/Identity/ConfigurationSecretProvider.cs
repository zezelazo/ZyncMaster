using Microsoft.Extensions.Configuration;

namespace ZyncMaster.Server;

public sealed class ConfigurationSecretProvider : ISecretProvider
{
    private readonly IConfiguration _config;

    public ConfigurationSecretProvider(IConfiguration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public string GetMicrosoftClientSecret() => _config["Microsoft:ClientSecret"] ?? "";
}
