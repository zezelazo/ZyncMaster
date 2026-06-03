using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace ZyncMaster.Server;

public sealed class DeviceService
{
    private const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int CodeLength = 6;

    private readonly IDeviceStore _store;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly IUserStore _users;
    private readonly DeviceNameGenerator _nameGenerator;
    private readonly ServerOptions _options;

    public DeviceService(
        IDeviceStore store,
        ICurrentUserAccessor currentUser,
        IUserStore users,
        DeviceNameGenerator nameGenerator,
        IOptions<ServerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(nameGenerator);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _currentUser = currentUser;
        _users = users;
        _nameGenerator = nameGenerator;
        _options = options.Value;
    }

    private int LeaseTtlMinutes => _options.DeviceLeaseTtlMinutes <= 0 ? 10 : _options.DeviceLeaseTtlMinutes;

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

        var generated = ApiKeyGenerator.GenerateKey();
        var device = new Device
        {
            Id = Guid.NewGuid().ToString("N"),
            // §A-2 — bind the device to the REAL approving user from the ambient identity, not to
            // the seeded "default". Approve runs under the panel cookie, so _currentUser.UserId is
            // the signed-in approver. This fixes the historical bug where a device created here had
            // no UserId and was silently attached to the seeded default user.
            UserId = _currentUser.UserId,
            Name = pending.DeviceName,
            ApiKeyHash = ApiKeyHasher.Hash(generated.Secret),
            KeyId = generated.KeyId,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        await _store.AddAsync(device, ct);

        var updated = pending with
        {
            Approved = true,
            ApprovedDeviceId = device.Id,
            OneTimeApiKey = generated.ApiKey,
        };
        await _store.UpdatePendingAsync(updated, ct);

        return true;
    }

    // §A-2 — brokered device registration. The caller is authenticated by an identity bearer, so
    // the device is bound to the TOKEN's user (resolved via ICurrentUserAccessor), NEVER to a
    // userId from the request body. The body only carries device metadata. Generates a fresh
    // "keyId.secret" key (the secret is returned once, only its hash is stored) and an initial
    // lease so the just-registered App is immediately treated as running.
    public async Task<DeviceRegisterResult> RegisterAsync(DeviceRegisterRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var generated = ApiKeyGenerator.GenerateKey();
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.AddMinutes(LeaseTtlMinutes);

        // When the App registers without a name, mint a friendly, unique geek name derived from the
        // user's account (email local-part / display name) so every device has a readable handle the
        // user can later rename. Unique among the user's existing device names.
        var name = string.IsNullOrWhiteSpace(request.Name)
            ? await GenerateUniqueNameAsync(ct)
            : request.Name.Trim();

        var device = new Device
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = _currentUser.UserId,
            Name = name,
            ApiKeyHash = ApiKeyHasher.Hash(generated.Secret),
            KeyId = generated.KeyId,
            Platform = NormalizePlatform(request.Platform),
            HasOutlookCom = request.HasOutlookCom,
            AppVersion = string.IsNullOrWhiteSpace(request.AppVersion) ? null : request.AppVersion.Trim(),
            CreatedUtc = now,
            LastSeenUtc = now,
            LeaseUntil = leaseUntil,
        };
        await _store.AddAsync(device, ct);

        return new DeviceRegisterResult
        {
            DeviceId = device.Id,
            ApiKey = generated.ApiKey,
            LeaseUntil = leaseUntil,
        };
    }

    // Renews the lease for the device identified by the api-key principal. Returns null when the
    // device is unknown (should not happen once the request passed ApiKey auth, but the store is
    // user-scoped so a race that removed the device resolves to null). LastSeenUtc is bumped too.
    public async Task<DeviceHeartbeatResult?> HeartbeatAsync(string deviceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        var device = await _store.GetAsync(deviceId, ct);
        if (device is null)
            return null;

        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.AddMinutes(LeaseTtlMinutes);
        await _store.UpdateAsync(device with { LeaseUntil = leaseUntil, LastSeenUtc = now }, ct);

        return new DeviceHeartbeatResult { LeaseUntil = leaseUntil };
    }

    // Renames the device identified by the api-key principal to itself. The deviceId is the
    // caller's own (from the principal, NEVER from the body); the store is user-scoped so the
    // lookup + update can only ever touch a device owned by the caller's user. Returns null when
    // the device is unknown (a race that removed it after auth). The name is trimmed before save;
    // the endpoint validates it is non-empty and <=100 chars (post-trim).
    public async Task<DeviceRenameResult?> RenameAsync(string deviceId, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Device name is required.", nameof(name));

        var device = await _store.GetAsync(deviceId, ct);
        if (device is null)
            return null;

        var trimmed = name.Trim();
        await _store.UpdateAsync(device with { Name = trimmed }, ct);

        return new DeviceRenameResult { DeviceId = device.Id, Name = trimmed };
    }

    // Returns the device identified by the api-key principal (the caller's own device), or null if
    // it no longer exists. User-scoped through the store, so it can only ever return the caller's
    // device — used by the App to pre-load the real current name into Settings.
    public async Task<Device?> GetMeAsync(string deviceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        var device = await _store.GetAsync(deviceId, ct);
        if (device is null)
            return null;

        // Self-heal legacy / nameless devices: if the device has no name yet, mint a unique geek
        // name now and persist it, so old rows created before name generation existed get backfilled
        // the first time the App reads itself. The generated name excludes THIS device's (empty) name
        // from the uniqueness set, which is a no-op since it is blank.
        if (string.IsNullOrWhiteSpace(device.Name))
        {
            var name = await GenerateUniqueNameAsync(ct, excludeDeviceId: device.Id);
            var updated = device with { Name = name };
            await _store.UpdateAsync(updated, ct);
            return updated;
        }

        return device;
    }

    // Builds a unique geek name for a new/nameless device of the current user. Resolves the account
    // identifier (PrimaryEmail / legacy Email / DisplayName / userId) and the set of names already
    // taken by the user's devices, then delegates to the pure DeviceNameGenerator. excludeDeviceId
    // lets the /me self-heal path ignore the device being renamed when building the taken-set.
    private async Task<string> GenerateUniqueNameAsync(CancellationToken ct, string? excludeDeviceId = null)
    {
        var accountId = await ResolveAccountIdentifierAsync(ct);

        var devices = await _store.ListAsync(ct);
        var existing = devices
            .Where(d => excludeDeviceId is null || d.Id != excludeDeviceId)
            .Select(d => d.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList();

        return _nameGenerator.Generate(accountId, existing);
    }

    // Best-effort account identifier for slug derivation: prefer the canonical PrimaryEmail, then
    // the legacy per-login Email, then the DisplayName, finally the raw userId so a slug always
    // exists even for the seeded/default user or when the user row is missing.
    private async Task<string> ResolveAccountIdentifierAsync(CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        var user = await _users.GetAsync(userId, ct);
        if (user is null)
            return userId;

        if (!string.IsNullOrWhiteSpace(user.PrimaryEmail))
            return user.PrimaryEmail;
        if (!string.IsNullOrWhiteSpace(user.Email))
            return user.Email!;
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
            return user.DisplayName!;
        return userId;
    }

    private static string NormalizePlatform(string? platform)
    {
        var p = platform?.Trim().ToLowerInvariant();
        return p is "windows" or "macos" or "linux" ? p : "windows";
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
