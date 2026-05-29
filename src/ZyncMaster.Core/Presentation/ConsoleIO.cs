using System;

namespace ZyncMaster.Core;

public sealed class ConsoleIO : IConsoleIO
{
    public void Write(string text)
        => Console.Write(text);

    public void WriteLine(string? text = null)
        => Console.WriteLine(text);

    public void WriteError(string text)
        => Console.Error.WriteLine(text);

    public string? ReadLine()
        => Console.ReadLine();
}
