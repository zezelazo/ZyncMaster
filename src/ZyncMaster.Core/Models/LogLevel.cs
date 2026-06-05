namespace ZyncMaster.Core;

// Severity levels for the device-side local logger, ordered ascending. The configured
// minimum level filters out anything below it: the default is Warning (Warning + Error),
// and the --verbose flag lowers it to Debug (everything).
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}
