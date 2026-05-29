using System.Diagnostics.CodeAnalysis;

namespace ZyncMaster.Core;

public interface IApplicationTerminator
{
    [DoesNotReturn] void Exit(int code);
    [DoesNotReturn] void ExitWithError(string message, int code = 1);
}
