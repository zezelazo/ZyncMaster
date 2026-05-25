namespace SyncMaster.Core;

public interface IConsoleIO
{
    void    Write(string text);
    void    WriteLine(string? text = null);
    void    WriteError(string text);
    string? ReadLine();
}
