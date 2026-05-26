using System;

namespace SyncMaster.Core;

public sealed class CalendarReadException : Exception
{
    public CalendarReadException(string message) : base(message) { }
    public CalendarReadException(string message, Exception inner) : base(message, inner) { }
}
