using System;
using ZyncMaster.Engine;

namespace ZyncMaster.App.Platform;

// Selects the right IDeviceKeyStore for the host platform:
//   Windows -> DpapiDeviceKeyStore (DPAPI, encrypted at rest, current user)
//   macOS   -> KeychainDeviceKeyStore (login keychain via the `security` CLI)
//   else    -> FileDeviceKeyStore (base64 file fallback; not encrypted at rest)
// The platform is injected so the selection is unit-testable without running on each OS.
public static class KeyStoreFactory
{
    public static IDeviceKeyStore Create(IPlatform platform, string fileFallbackPath, IClock clock)
    {
        if (platform == null) throw new ArgumentNullException(nameof(platform));
        if (fileFallbackPath == null) throw new ArgumentNullException(nameof(fileFallbackPath));
        if (clock == null) throw new ArgumentNullException(nameof(clock));

        if (platform.IsWindows)
            return new DpapiDeviceKeyStore(fileFallbackPath, clock);

        if (platform.IsMacOS)
            return new KeychainDeviceKeyStore();

        return new FileDeviceKeyStore(fileFallbackPath);
    }
}
