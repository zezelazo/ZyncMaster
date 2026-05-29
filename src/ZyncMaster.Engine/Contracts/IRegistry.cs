namespace ZyncMaster.Engine;

// Minimal seam over the Windows registry so registry-backed components can be unit
// tested with a fake. Operates on a HKCU subkey path and a named string value.
public interface IRegistry
{
    // Returns the string value, or null if the key or value does not exist.
    string? GetValue(string subKeyPath, string valueName);

    // Creates the subkey if needed and writes the string value.
    void SetValue(string subKeyPath, string valueName, string value);

    // Removes the named value if present; a no-op otherwise.
    void DeleteValue(string subKeyPath, string valueName);
}
