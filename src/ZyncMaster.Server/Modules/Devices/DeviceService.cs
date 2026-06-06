using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ZyncMaster.Server;

// Thrown when a rename/register would land on a device name already taken by ANOTHER device of the
// same user (case-insensitive). The endpoint maps this to a 409 with error code "name_taken" so the
// UI can show an inline "Name already used" message rather than an opaque 500.
public sealed class DeviceNameTakenException : Exception
{
    public DeviceNameTakenException(string name)
        : base($"The device name '{name}' is already used by another device.") { }
}

public sealed class DeviceService
{
    // Crockford-style 32-char alphabet (no I/L/O/U to avoid look-alikes). At CodeLength chars the
    // entropy is CodeLength * log2(32) = CodeLength * 5 bits.
    private const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    // FIX A — raised from 6 to 8 chars: 8 * 5 = 40 bits of entropy (was 30). Combined with the
    // pairing-code TTL and the per-IP rate limiter, this makes online brute-forcing a live code
    // infeasible. Exposed internally so the entropy can be asserted by tests.
    internal const int CodeLength = 8;
    internal const int CodeEntropyBits = CodeLength * 5;

    // A few register retries are enough to step past a same-user collision on the generated name: a
    // single user racing several nameless registrations at once is rare, and each retry regenerates
    // from the freshly re-read taken-set so it picks the next free character/suffix.
    private const int RegisterCollisionRetries = 5;

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

    private int PendingPairingTtlMinutes =>
        _options.PendingPairingTtlMinutes <= 0 ? 15 : _options.PendingPairingTtlMinutes;

    // Cutoff before which a pending pairing is expired (CreatedUtc must be >= this to be live).
    private DateTimeOffset PairingCutoff(DateTimeOffset now) => now.AddMinutes(-PendingPairingTtlMinutes);

    public async Task<PairStartResult> StartPairingAsync(string deviceName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("Device name is required.", nameof(deviceName));

        var pairingId = Guid.NewGuid().ToString("N");
        var code = GenerateCode();

        // FIX 1 — mint a high-entropy PKCE verifier (256 bits). The clear verifier is returned to
        // the caller ONCE (PairStartResult.Verifier); only its SHA-256 is persisted. /api/pair/
        // complete must present a verifier whose hash matches before the api key is released, so a
        // third party who only learns the pairingId cannot complete the handshake.
        var verifier = PairingVerifier.Generate();

        var pending = new PendingPairing
        {
            PairingId = pairingId,
            DeviceName = deviceName,
            Code = code,
            Approved = false,
            CreatedUtc = DateTimeOffset.UtcNow,
            VerifierHash = PairingVerifier.Hash(verifier),
        };
        await _store.SavePendingAsync(pending, ct);

        return new PairStartResult { PairingId = pairingId, Code = code, Verifier = verifier };
    }

    // FIX A — atomic, idempotent approve. Two defects are closed here:
    //   * EXPIRY: the code is resolved through the TTL-bounded lookup, so an expired code is rejected.
    //   * DOUBLE-APPROVE: the row is claimed with an atomic conditional UPDATE
    //     (Approved 0 -> 1) BEFORE the device is created; the device is only persisted when THIS
    //     call won the claim. A second approve of the same code (concurrent or sequential) loses the
    //     claim and returns true WITHOUT creating a second DeviceRow or overwriting the live
    //     OneTimeApiKey — so the historical phantom-device / key-clobber bug cannot happen.
    public async Task<bool> ApproveAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var now = DateTimeOffset.UtcNow;
        var cutoff = PairingCutoff(now);

        // Resolve the row (TTL-checked) to recover the device name and reject expired/unknown codes
        // up front. The actual claim is the atomic conditional UPDATE below.
        var pending = await _store.GetPendingByCodeAsync(code, cutoff, ct);
        if (pending is null)
            return false;

        // Already approved -> idempotent success: do NOT create another device or re-issue a key.
        if (pending.Approved)
            return true;

        var generated = ApiKeyGenerator.GenerateKey();
        var deviceId = Guid.NewGuid().ToString("N");

        // Atomic claim FIRST. Only the winner (exactly one row updated) proceeds to create the
        // device. A racing/repeat approve gets `false` and creates nothing.
        var claimed = await _store.TryMarkApprovedAsync(code, cutoff, deviceId, generated.ApiKey, ct);
        if (!claimed)
            // Lost the race (another approve already claimed it) or it expired between the read and
            // the update. Idempotent: report success when the row is now approved, false otherwise.
            return (await _store.GetPendingByCodeAsync(code, cutoff, ct))?.Approved ?? false;

        var device = new Device
        {
            Id = deviceId,
            // §A-2 — bind the device to the REAL approving user from the ambient identity, not to
            // the seeded "default". Approve runs under the panel cookie, so _currentUser.UserId is
            // the signed-in approver. This fixes the historical bug where a device created here had
            // no UserId and was silently attached to the seeded default user.
            UserId = _currentUser.UserId,
            Name = pending.DeviceName,
            ApiKeyHash = ApiKeyHasher.Hash(generated.Secret),
            KeyId = generated.KeyId,
            CreatedUtc = now,
        };
        await _store.AddAsync(device, ct);

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

        var explicitName = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim();

        // Insert with a retry loop so a same-user collision on the unique (UserId, NameLower) index
        // cannot fail the registration. For a generated (nameless) request we regenerate from the
        // freshly re-read taken-set each attempt (it picks the next free character/suffix); for an
        // explicit name we surface a clean "name_taken" instead of looping (the user must choose).
        for (var attempt = 0; ; attempt++)
        {
            // When the App registers without a name, mint a friendly, unique geek name derived from
            // the user's account (email local-part / display name) so every device has a readable
            // handle the user can later rename. Unique among the user's existing device names.
            var name = explicitName ?? await GenerateUniqueNameAsync(ct);

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

            try
            {
                await _store.AddAsync(device, ct);
            }
            catch (DbUpdateException ex) when (IsNameConflict(ex))
            {
                // An explicit name collided: nothing to regenerate, so report it to the caller.
                if (explicitName is not null)
                    throw new DeviceNameTakenException(explicitName);

                // A generated name raced another registration of the same user. Regenerate and retry
                // a bounded number of times; if even that is exhausted, rethrow the underlying error.
                if (attempt >= RegisterCollisionRetries)
                    throw;
                continue;
            }

            return new DeviceRegisterResult
            {
                DeviceId = device.Id,
                ApiKey = generated.ApiKey,
                LeaseUntil = leaseUntil,
            };
        }
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

        // Reject a name already used by ANOTHER device of the same user (case-insensitive); keeping
        // the device's own current name is always fine (no self-collision). This is the explicit
        // pre-check; the unique index is the backstop against the concurrent race, surfaced below as
        // the same name_taken error.
        if (!await IsNameAvailableAsync(trimmed, excludeDeviceId: device.Id, ct))
            throw new DeviceNameTakenException(trimmed);

        try
        {
            await _store.UpdateAsync(device with { Name = trimmed }, ct);
        }
        catch (DbUpdateException ex) when (IsNameConflict(ex))
        {
            throw new DeviceNameTakenException(trimmed);
        }

        return new DeviceRenameResult { DeviceId = device.Id, Name = trimmed };
    }

    // True when `name` (trimmed) is free for the current user's device pool, case-insensitive,
    // EXCLUDING excludeDeviceId (the caller's own device) — so re-typing the device's current name
    // reports available. A blank or over-long name is NOT available (the caller treats it as
    // invalid). Used by the live availability endpoint and as the rename pre-check.
    public async Task<bool> IsNameAvailableAsync(string name, string? excludeDeviceId, CancellationToken ct = default)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > DeviceNameGenerator.MaxNameLength)
            return false;

        var key = trimmed.ToLowerInvariant();
        var devices = await _store.ListAsync(ct);
        return !devices.Any(d =>
            (excludeDeviceId is null || d.Id != excludeDeviceId)
            && string.Equals((d.Name ?? string.Empty).Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    // True when a DbUpdateException is the unique-constraint violation on the per-user device-name
    // index. Detected by the provider's NUMERIC error code on the INNER exception — NEVER by the
    // message text, which drifts across EF/provider versions and is not even produced by SQLite in
    // English. Works on BOTH SQL Server (prod) and SQLite (tests) without a hard reference to either
    // provider's exception type: the inner exception is matched by type name + reflected code props.
    //   * SQL Server: SqlException.Number 2601 (duplicate-key index) or 2627 (unique constraint).
    //   * SQLite:     SqliteException.SqliteErrorCode 19 (SQLITE_CONSTRAINT) and/or
    //                 SqliteExtendedErrorCode 2067 (SQLITE_CONSTRAINT_UNIQUE).
    internal static bool IsNameConflict(DbUpdateException ex)
        => ex.InnerException is { } inner && IsUniqueConstraintViolation(inner);

    internal static bool IsUniqueConstraintViolation(Exception inner)
    {
        var typeName = inner.GetType().FullName ?? string.Empty;

        // SQL Server — Microsoft.Data.SqlClient.SqlException exposes an int Number.
        if (typeName == "Microsoft.Data.SqlClient.SqlException" ||
            typeName == "System.Data.SqlClient.SqlException")
        {
            var number = ReadIntProperty(inner, "Number");
            return number is 2601 or 2627;
        }

        // SQLite — Microsoft.Data.Sqlite.SqliteException exposes SqliteErrorCode (primary, 19 ==
        // SQLITE_CONSTRAINT) and SqliteExtendedErrorCode (2067 == SQLITE_CONSTRAINT_UNIQUE). Either
        // identifies the duplicate-index insert the tests trigger on the (UserId, NameLower) index.
        if (typeName == "Microsoft.Data.Sqlite.SqliteException")
        {
            var primary = ReadIntProperty(inner, "SqliteErrorCode");
            var extended = ReadIntProperty(inner, "SqliteExtendedErrorCode");
            return primary == 19 || extended == 2067;
        }

        return false;
    }

    // Reads an int property by name via reflection so DeviceService needs no compile-time reference
    // to the provider-specific exception types. Returns null when the property is absent/non-int.
    private static int? ReadIntProperty(object instance, string propertyName)
    {
        var prop = instance.GetType().GetProperty(propertyName);
        if (prop is null)
            return null;
        var value = prop.GetValue(instance);
        return value is int i ? i : null;
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

    // Returns the device with this id (user-scoped through the store), or null when it does not exist
    // or belongs to another user. Used by the COM-pin surface to resolve the pinned device's display
    // name and lease for the pair listing / request-sync routing. NO self-heal (unlike GetMeAsync):
    // this is a read of an arbitrary device of the user, not the caller's own.
    public async Task<Device?> GetByIdAsync(string deviceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;
        return await _store.GetAsync(deviceId, ct);
    }

    // Lists every device of the current user (user-scoped through the store). Used by the pair
    // listing to resolve the pinned-device name + online flag for all COM-pinned pairs with a single
    // read rather than one GetById per pair.
    public Task<IReadOnlyList<Device>> ListForCurrentUserAsync(CancellationToken ct = default)
        => _store.ListAsync(ct);

    // True when the device identified by deviceId currently holds a live lease (the App is running on
    // it): the device exists for this user AND LeaseUntil > now. Drives the COM-pin "origin online?"
    // check for /request-sync and the pair listing's pinnedDeviceOnline. A device with no lease, an
    // expired lease, an unknown id, or a foreign id all resolve to false (offline).
    public async Task<bool> IsDeviceOnlineAsync(string deviceId, CancellationToken ct = default)
    {
        var device = await GetByIdAsync(deviceId, ct);
        return device?.LeaseUntil is { } until && until > DateTimeOffset.UtcNow;
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

    public async Task<PairCompleteResult> CompletePairingAsync(
        string pairingId, string? verifier = null, CancellationToken ct = default)
    {
        var pending = await _store.GetPendingAsync(pairingId, ct);
        if (pending is null || !pending.Approved)
            return new PairCompleteResult { Approved = pending?.Approved ?? false };

        // FIX 1 — PKCE check: the api key is released ONLY to a caller that proves possession of the
        // verifier minted at /api/pair/start (its hash is stored on the row). A wrong/absent verifier
        // is rejected as NOT approved, so a third party who learned the pairingId cannot harvest the
        // victim's key. The comparison is constant-time over the stored hash. Legacy rows with no
        // stored hash (pre-FIX-1, none minted after this change) keep the old behaviour so any
        // in-flight pairing still completes; new rows always carry a hash and are always enforced.
        if (!string.IsNullOrEmpty(pending.VerifierHash))
        {
            if (string.IsNullOrEmpty(verifier)
                || !PairingVerifier.Matches(verifier, pending.VerifierHash))
            {
                // Do NOT reveal whether the pairing exists/was approved — report "not approved",
                // the same shape an un-approved or unknown pairing returns.
                return new PairCompleteResult { Approved = false };
            }
        }

        // The one-time key is handed out exactly once: cleared in place so a second complete is
        // idempotent (Approved=true, ApiKey=null) — the App may legitimately re-poll. The now-spent
        // row carries no live secret; it is reaped by the TTL-bounded ephemeral purge (FIX A), which
        // closes the cycle without breaking that re-poll idempotency. (Deleting the row here instead
        // would make a re-poll report "not approved", which the App treats as a failed pairing.)
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
