using System;
using System.Diagnostics.CodeAnalysis;

namespace SyncMaster.Core;

public sealed class ConsoleApplicationTerminator : IApplicationTerminator
{
    private readonly IConsoleIO _console;

    public ConsoleApplicationTerminator(IConsoleIO console)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    [DoesNotReturn]
    public void Exit(int code)
    {
        Environment.Exit(code);
        throw new InvalidOperationException("Unreachable");
    }

    [DoesNotReturn]
    public void ExitWithError(string message, int code = 1)
    {
        _console.WriteError(message);
        Environment.Exit(code);
        throw new InvalidOperationException("Unreachable");
    }
}
