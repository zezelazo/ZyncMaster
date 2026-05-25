using System;

namespace SyncMaster.CalExport;

public sealed class ArgumentParsingException : Exception
{
    public ArgumentParsingException(string message) : base(message) { }
    public ArgumentParsingException(string message, Exception inner) : base(message, inner) { }
}

public sealed class ArgumentParser
{
    public ParsedArguments Parse(string[] args)
    {
        if (args == null) throw new ArgumentNullException(nameof(args));

        bool    autoMode   = false;
        string? configPath = null;
        string? outputPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
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

                case "-o":
                case "--output":
                    if (i + 1 >= args.Length)
                        throw new ArgumentParsingException("-o/--output requires a path argument.");
                    outputPath = args[++i];
                    break;

                default:
                    throw new ArgumentParsingException($"Unknown argument '{args[i]}'.");
            }
        }

        return new ParsedArguments
        {
            AutoMode   = autoMode,
            ConfigPath = configPath,
            OutputPath = outputPath,
        };
    }
}
