namespace ZyncMaster.CalExport;

// Process exit codes CalExport returns to its caller (the Engine reads these to tell apart a
// recoverable environment problem from a generic failure). Kept in one place so the contract is
// explicit and unit-testable.
public static class ExitCodes
{
    // Clean success.
    public const int Success = 0;

    // Generic / unexpected failure (malformed config, IO error, unhandled COM error). Matches the
    // historic exit(1) so existing behaviour is unchanged for every non-Outlook-availability case.
    public const int GeneralError = 1;

    // Bad command-line arguments (argument parsing failed). Distinct from a runtime failure.
    public const int InvalidArguments = 2;

    // Outlook Classic is not installed/registered or could not be started at all. DISTINGUISHABLE so
    // the App can show a friendly "Outlook is not available on this device" message instead of a
    // raw stack trace, and so a future scheduler can treat it as a non-retryable environment issue.
    public const int OutlookUnavailable = 3;
}
