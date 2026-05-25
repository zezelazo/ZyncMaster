using System;

namespace SyncMaster.CalImport;

public sealed class ImportSourceException : Exception
{
    public ImportSourceException(string message) : base(message) { }
    public ImportSourceException(string message, Exception inner) : base(message, inner) { }
}
