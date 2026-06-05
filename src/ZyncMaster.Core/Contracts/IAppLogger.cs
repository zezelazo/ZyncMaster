using System;

namespace ZyncMaster.Core;

// Minimal logging abstraction for the device-side tools (App, Engine, CalExport, Cli).
//
// Deliberately tiny and coherent with the repo's manual constructor injection: it is passed
// down through constructors exactly like ICalendarSource / IPairsClient, and the concrete
// implementation is swapped in one line in each composition root. Microsoft.Extensions.Logging
// is intentionally NOT used here — it would pull in a DI container model that the device-side
// code does not have (the Server, which does use the native DI container, keeps using ILogger).
public interface IAppLogger
{
    // Writes a single entry at the given level. The implementation drops it when level is below
    // the configured minimum. ex (when supplied) is appended after the message.
    void Log(LogLevel level, string message, Exception? ex = null);

    // True when an entry at this level would be written. Callers use it to skip building an
    // expensive verbose message when verbose logging is off.
    bool IsEnabled(LogLevel level);
}
