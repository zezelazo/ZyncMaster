using System;

namespace SyncMaster.CalImport;

public sealed class ImportSettingsResolver
{
    private const string DefaultAuthority = "https://login.microsoftonline.com/consumers";

    public string ResolveAuthority(AppConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(config.Authority))
            return DefaultAuthority;
        if (!Uri.TryCreate(config.Authority, UriKind.Absolute, out _))
            throw new SettingsValidationException(
                $"'authority' in appsettings.json is not a valid absolute URI: '{config.Authority}'. " +
                "Fix the value (for example 'https://login.microsoftonline.com/consumers') " +
                "or remove the field to use the default.");
        return config.Authority;
    }

    public Guid ResolveExtendedPropertyGuid(AppConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrEmpty(config.ExtendedPropertyGuid))
            return new Guid(AppConfig.DefaultExtendedPropertyGuid);
        if (!Guid.TryParse(config.ExtendedPropertyGuid, out var g))
            throw new SettingsValidationException(
                $"'extendedPropertyGuid' in appsettings.json is not a valid GUID: '{config.ExtendedPropertyGuid}'. " +
                "Fix the value or remove the field to use the default. " +
                "WARNING: changing this GUID makes previously-imported events invisible to CalImport.");
        return g;
    }

    public int ResolveReminderMinutes(ImportSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return settings.ReminderMinutes < 0 ? 30 : settings.ReminderMinutes;
    }
}
