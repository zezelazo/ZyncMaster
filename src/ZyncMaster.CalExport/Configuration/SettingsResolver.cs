using System;
using Newtonsoft.Json.Linq;
using ZyncMaster.Core;

namespace ZyncMaster.CalExport;

public sealed class SettingsResolver
{
    /// <summary>Resolves the year from AppSettings. Handles "current", "previous", int, or numeric string.</summary>
    public int ResolveYear(AppSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        if (settings.Year is JValue jv)
        {
            if (jv.Type == JTokenType.Integer)
                return jv.Value<int>();

            return (jv.Value<string>() ?? "") switch
            {
                "current"  => DateTime.Today.Year,
                "previous" => DateTime.Today.Year - 1,
                var s      => int.TryParse(s, out int y) ? y : DateTime.Today.Year,
            };
        }

        return DateTime.Today.Year;
    }

    /// <summary>Resolves the month from AppSettings. Handles "current", "previous", int 1-12, or numeric string.</summary>
    public int ResolveMonth(AppSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        if (settings.Month is JValue jv)
        {
            if (jv.Type == JTokenType.Integer)
            {
                int n = jv.Value<int>();
                return n >= 1 && n <= 12 ? n : DateTime.Today.Month;
            }

            var s = jv.Value<string>() ?? "";

            if (s.Equals("current", StringComparison.OrdinalIgnoreCase))
                return DateTime.Today.Month;

            if (s.Equals("previous", StringComparison.OrdinalIgnoreCase))
                return DateTime.Today.AddMonths(-1).Month;

            if (int.TryParse(s, out int m) && m >= 1 && m <= 12)
                return m;
        }

        return DateTime.Today.Month;
    }

    /// <summary>Resolves the export mode. "simple" → Simple, anything else → Complete.</summary>
    public ExportMode ResolveMode(AppSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        return (settings.Mode ?? "").Equals("simple", StringComparison.OrdinalIgnoreCase)
            ? ExportMode.Simple
            : ExportMode.Complete;
    }

    /// <summary>Resolves calendar names. "all" → null; array → string[].</summary>
    public string[]? ResolveCalendarNames(AppSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        if (settings.Calendars is JValue jv &&
            string.Equals(jv.Value<string>(), "all", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            return settings.Calendars?.ToObject<string[]>();
        }
        catch (Newtonsoft.Json.JsonSerializationException)
        {
            return null;
        }
    }
}
