namespace ZyncMaster.Engine;

// Controls whether the sync host launches automatically when the user logs in.
public interface IAutoStartManager
{
    bool IsEnabled();
    void Enable(string exePath, string args);
    void Disable();
}
