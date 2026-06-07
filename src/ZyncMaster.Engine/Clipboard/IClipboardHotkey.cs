namespace ZyncMaster.Engine;

// Global hotkey that opens the clipboard viewer. Registration is platform-specific (App layer).
public interface IClipboardHotkey
{
    event Action Pressed;
    void Register(string hotkey);
    void Unregister();
}
