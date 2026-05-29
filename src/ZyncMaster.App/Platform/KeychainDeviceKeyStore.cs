using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.Engine;

namespace ZyncMaster.App.Platform;

// macOS Keychain-backed device key store, driven through the `security` CLI via
// Process. This is an untested process boundary (like DefaultBrowserLauncher /
// CalExportRunner): it shells out to /usr/bin/security and so can only be exercised on
// a real Mac. KeyStoreFactory selects it on macOS; everywhere else it is never used.
//
// The key is stored as a generic password under a fixed service + account so it
// survives reinstalls and is scoped to the user's login keychain.
public sealed class KeychainDeviceKeyStore : IDeviceKeyStore
{
    private const string SecurityTool = "/usr/bin/security";

    private readonly string _service;
    private readonly string _account;

    public KeychainDeviceKeyStore(string service = "ZyncMaster", string account = "device-api-key")
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _account = account ?? throw new ArgumentNullException(nameof(account));
    }

    public async Task SaveAsync(string apiKey, CancellationToken ct)
    {
        if (apiKey == null) throw new ArgumentNullException(nameof(apiKey));

        // -U updates the item if it already exists rather than failing.
        await RunAsync(ct,
            "add-generic-password", "-U",
            "-s", _service,
            "-a", _account,
            "-w", apiKey);
    }

    public async Task<string?> LoadAsync(CancellationToken ct)
    {
        var (exitCode, stdout, _) = await TryRunAsync(ct,
            "find-generic-password",
            "-s", _service,
            "-a", _account,
            "-w");

        if (exitCode != 0)
            return null; // item not found -> not paired

        var value = stdout.TrimEnd('\n', '\r');
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        await TryRunAsync(ct,
            "delete-generic-password",
            "-s", _service,
            "-a", _account);
    }

    private static async Task RunAsync(CancellationToken ct, params string[] args)
    {
        var (exitCode, _, stderr) = await TryRunAsync(ct, args);
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"`security {string.Join(' ', args)}` failed with exit code {exitCode}: {stderr}".TrimEnd());
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> TryRunAsync(
        CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = SecurityTool,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start '{SecurityTool}'.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
