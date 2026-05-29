using System.Security.Cryptography;

namespace ZyncMaster.Server;

public sealed class DeviceService
{
    private const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int CodeLength = 6;

    private readonly IDeviceStore _store;

    public DeviceService(IDeviceStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task<PairStartResult> StartPairingAsync(string deviceName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("Device name is required.", nameof(deviceName));

        var pairingId = Guid.NewGuid().ToString("N");
        var code = GenerateCode();

        var pending = new PendingPairing
        {
            PairingId = pairingId,
            DeviceName = deviceName,
            Code = code,
            Approved = false,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        await _store.SavePendingAsync(pending, ct);

        return new PairStartResult { PairingId = pairingId, Code = code };
    }

    public async Task<bool> ApproveAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var pending = await _store.GetPendingByCodeAsync(code, ct);
        if (pending is null)
            return false;

        var key = ApiKeyGenerator.Generate();
        var device = new Device
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = pending.DeviceName,
            ApiKeyHash = ApiKeyHasher.Hash(key),
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        await _store.AddAsync(device, ct);

        var updated = pending with
        {
            Approved = true,
            ApprovedDeviceId = device.Id,
            OneTimeApiKey = key,
        };
        await _store.UpdatePendingAsync(updated, ct);

        return true;
    }

    public async Task<PairCompleteResult> CompletePairingAsync(string pairingId, CancellationToken ct = default)
    {
        var pending = await _store.GetPendingAsync(pairingId, ct);
        if (pending is null || !pending.Approved)
            return new PairCompleteResult { Approved = pending?.Approved ?? false };

        var key = pending.OneTimeApiKey;
        var updated = pending with { OneTimeApiKey = null };
        await _store.UpdatePendingAsync(updated, ct);

        return new PairCompleteResult
        {
            Approved = true,
            ApiKey = key,
            DeviceId = pending.ApprovedDeviceId,
        };
    }

    private static string GenerateCode()
    {
        Span<char> chars = stackalloc char[CodeLength];
        for (var i = 0; i < CodeLength; i++)
        {
            var idx = RandomNumberGenerator.GetInt32(CodeAlphabet.Length);
            chars[i] = CodeAlphabet[idx];
        }
        return new string(chars);
    }
}
