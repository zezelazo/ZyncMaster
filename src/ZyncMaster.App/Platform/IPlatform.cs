namespace ZyncMaster.App.Platform;

// Abstraction over the host OS so KeyStoreFactory can be unit-tested with a fake
// platform instead of branching directly on OperatingSystem.
public interface IPlatform
{
    bool IsWindows { get; }
    bool IsMacOS { get; }
}

// Real implementation over OperatingSystem.
public sealed class DefaultPlatform : IPlatform
{
    public bool IsWindows => OperatingSystem.IsWindows();
    public bool IsMacOS => OperatingSystem.IsMacOS();
}
