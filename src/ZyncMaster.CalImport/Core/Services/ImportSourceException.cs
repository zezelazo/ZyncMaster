using System;

namespace ZyncMaster.CalImport;

public sealed class ImportSourceException : Exception
{
    public ImportSourceException(string message) : base(message) { }
    public ImportSourceException(string message, Exception inner) : base(message, inner) { }
}
