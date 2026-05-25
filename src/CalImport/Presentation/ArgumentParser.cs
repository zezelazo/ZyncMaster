using System;

namespace SyncMaster.CalImport;

public sealed class ArgumentParsingException : Exception
{
    public ArgumentParsingException(string message) : base(message) { }
}

public sealed class ArgumentParser
{
    public ParsedImportArguments Parse(string[] args)
    {
        if (args == null) throw new ArgumentNullException(nameof(args));

        string? sourcePath      = null;
        string? configPath      = null;
        string? calendarId      = null;
        string? newCalendarName = null;
        bool    autoMode        = false;
        bool    dryRun          = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-s":
                case "--source":
                    if (i + 1 >= args.Length)
                        throw new ArgumentParsingException("-s/--source requires a path argument.");
                    sourcePath = args[++i];
                    break;

                case "-a":
                case "--auto":
                    autoMode = true;
                    break;

                case "-c":
                case "--config":
                    if (i + 1 >= args.Length)
                        throw new ArgumentParsingException("-c/--config requires a path argument.");
                    configPath = args[++i];
                    break;

                case "-k":
                case "--calendar":
                    if (i + 1 >= args.Length)
                        throw new ArgumentParsingException("-k/--calendar requires a calendar id argument.");
                    calendarId = args[++i];
                    break;

                case "-n":
                case "--new-calendar":
                    if (i + 1 >= args.Length)
                        throw new ArgumentParsingException("-n/--new-calendar requires a name argument.");
                    newCalendarName = args[++i];
                    break;

                case "--dry-run":
                    dryRun = true;
                    break;

                default:
                    throw new ArgumentParsingException($"Unknown argument '{args[i]}'.");
            }
        }

        if (calendarId != null && newCalendarName != null)
            throw new ArgumentParsingException("-k/--calendar and -n/--new-calendar are mutually exclusive.");

        return new ParsedImportArguments
        {
            SourcePath      = sourcePath,
            ConfigPath      = configPath,
            AutoMode        = autoMode,
            CalendarId      = calendarId,
            NewCalendarName = newCalendarName,
            DryRun          = dryRun,
        };
    }
}
