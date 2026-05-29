using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

// Orchestrates device pairing: start a pairing session, open the approval page in
// the browser, and poll until the server reports the device approved. On approval
// the issued API key is persisted through IDeviceKeyStore.
public sealed class PairingService
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(3);
    private const int DefaultMaxAttempts = 60;

    private readonly IPairingClient _pairing;
    private readonly IBrowserLauncher _browser;
    private readonly IDeviceKeyStore _keys;
    private readonly EngineSettings _settings;

    public PairingService(IPairingClient pairing, IBrowserLauncher browser, IDeviceKeyStore keys, EngineSettings settings)
    {
        _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    // Returns the existing key untouched if the device is already paired; otherwise
    // runs the full interactive pairing flow.
    public async Task<PairingOutcome> EnsurePairedAsync(CancellationToken ct = default)
    {
        var existing = await _keys.LoadAsync(ct);
        if (!string.IsNullOrEmpty(existing))
            return new PairingOutcome { Success = true, ApiKey = existing };

        return await PairAsync(ct: ct);
    }

    public async Task<PairingOutcome> PairAsync(
        TimeSpan? pollInterval = null, int? maxAttempts = null, CancellationToken ct = default)
    {
        var interval = pollInterval ?? DefaultPollInterval;
        var attempts = maxAttempts ?? DefaultMaxAttempts;

        var start = await _pairing.StartAsync(_settings.DeviceName, ct);

        var connectUrl = $"{_settings.ServerBaseUrl.TrimEnd('/')}/connect";
        _browser.Open(connectUrl);

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var result = await _pairing.CompleteAsync(start.PairingId, ct);
            if (result.Approved)
            {
                await _keys.SaveAsync(result.ApiKey!, ct);
                return new PairingOutcome
                {
                    Success = true,
                    ApiKey = result.ApiKey,
                    Code = start.Code,
                };
            }

            await Task.Delay(interval, ct);
        }

        return new PairingOutcome
        {
            Success = false,
            Message = "Pairing timed out waiting for approval.",
            Code = start.Code,
        };
    }
}
