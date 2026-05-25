using System;

namespace SyncMaster.CalImport;

public sealed class ImportSettingsResolver
{
    private const string DefaultAuthority = "https://login.microsoftonline.com/consumers";

    public string ResolveAuthority(ImportSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (string.IsNullOrWhiteSpace(settings.Authority))
            return DefaultAuthority;
        if (!Uri.TryCreate(settings.Authority, UriKind.Absolute, out _))
            throw new SettingsValidationException(
                $"'authority' in settings.json is not a valid absolute URI: '{settings.Authority}'. " +
                "Fix the value (for example 'https://login.microsoftonline.com/consumers') " +
                "or remove the field to use the default.");
        return settings.Authority;
    }

    public int ResolveReminderMinutes(ImportSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return settings.ReminderMinutes < 0 ? 30 : settings.ReminderMinutes;
    }

    public Guid ResolveExtendedPropertyGuid(ImportSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (string.IsNullOrEmpty(settings.ExtendedPropertyGuid))
            return new Guid(ImportSettings.DefaultExtendedPropertyGuid);
        if (!Guid.TryParse(settings.ExtendedPropertyGuid, out var g))
            throw new SettingsValidationException(
                $"'extendedPropertyGuid' in settings.json is not a valid GUID: '{settings.ExtendedPropertyGuid}'. " +
                "Fix the value or remove the field to use the default. " +
                "WARNING: changing this GUID makes previously-imported events invisible to CalImport.");
        return g;
    }
}
