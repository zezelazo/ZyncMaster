using System;
using System.IO;
using ZyncMaster.Core;

namespace ZyncMaster.CalExport;

public sealed class OutputDirectoryService
{
    private readonly IFileSystem           _fs;
    private readonly IConsoleIO            _console;
    private readonly IApplicationTerminator _terminator;

    public OutputDirectoryService(IFileSystem fs, IConsoleIO console, IApplicationTerminator terminator)
    {
        _fs         = fs         ?? throw new ArgumentNullException(nameof(fs));
        _console    = console    ?? throw new ArgumentNullException(nameof(console));
        _terminator = terminator ?? throw new ArgumentNullException(nameof(terminator));
    }

    public string Resolve(string? requestedPath, string fallbackPath, bool createSilently)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            return fallbackPath;

        var full = Path.GetFullPath(requestedPath);

        if (_fs.DirectoryExists(full))
            return full;

        _console.WriteLine($"Output directory '{full}' does not exist.");

        if (!createSilently)
        {
            _console.Write("Create it? [Y/n]: ");
            var ans = _console.ReadLine()?.Trim() ?? "";
            if (ans.Length > 0 && !ans.Equals("y", StringComparison.OrdinalIgnoreCase) &&
                !ans.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                _terminator.ExitWithError("Output directory is required. Exiting.");
                throw new InvalidOperationException("Unreachable");
            }
        }

        try
        {
            _fs.CreateDirectory(full);
            _console.WriteLine($"  Output directory created: {full}");
            return full;
        }
        catch (UnauthorizedAccessException)
        {
            _terminator.ExitWithError($"Error: Access denied creating '{full}'.");
            throw new InvalidOperationException("Unreachable");
        }
        catch (ArgumentException)
        {
            _terminator.ExitWithError($"Error: Invalid path '{full}'.");
            throw new InvalidOperationException("Unreachable");
        }
        catch (IOException ex)
        {
            _terminator.ExitWithError($"Error: Could not create '{full}' — {ex.Message}");
            throw new InvalidOperationException("Unreachable");
        }
    }
}
