using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ZyncMaster.Server.Tests.Devices;

public class DeviceServiceTests
{
    private static (DeviceService svc, InMemoryDeviceStore store) Build(
        ICurrentUserAccessor? user = null, ServerOptions? options = null, IUserStore? users = null)
    {
        var store = new InMemoryDeviceStore();
        var svc = new DeviceService(
            store,
            user ?? new DefaultCurrentUserAccessor(),
            users ?? new StubUserStore(),
            new DeviceNameGenerator(),
            Options.Create(options ?? new ServerOptions()));
        return (svc, store);
    }

    private sealed class FixedUser : ICurrentUserAccessor
    {
        public FixedUser(string userId) => UserId = userId;
        public string UserId { get; }
    }

    // Minimal IUserStore double for name generation. Returns no user (so the generator falls back to
    // the userId as the account identifier) unless a row was seeded by id.
    private sealed class StubUserStore : IUserStore
    {
        private readonly System.Collections.Generic.Dictionary<string, ZyncMaster.Server.Data.UserRow> _byId = new();

        public StubUserStore(params ZyncMaster.Server.Data.UserRow[] users)
        {
            foreach (var u in users)
                _byId[u.Id] = u;
        }

        public Task<ZyncMaster.Server.Data.UserRow?> GetAsync(string id, System.Threading.CancellationToken ct = default)
            => Task.FromResult(_byId.TryGetValue(id, out var row) ? row : null);

        public Task<ZyncMaster.Server.Data.UserRow> UpsertAsync(
            string provider, string subject, string email, string displayName, System.Threading.CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ZyncMaster.Server.Data.UserRow> UpsertByLoginAsync(
            string provider, string providerSubject, string email, bool emailVerified, string displayName,
            System.Threading.CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ZyncMaster.Server.Data.UserRow?> TryLinkByEmailAsync(
            string provider, string providerSubject, string email, bool emailVerified, string displayName,
            System.Threading.CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteUserAsync(string userId, System.Threading.CancellationToken ct = default)
            => Task.FromResult(_byId.Remove(userId));
    }

    [Fact]
    public void Ctor_null_store_throws()
    {
        Action act = () => new DeviceService(
            null!, new DefaultCurrentUserAccessor(), new StubUserStore(), new DeviceNameGenerator(),
            Options.Create(new ServerOptions()));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task StartPairing_creates_pending_with_persisted_id_and_code()
    {
        var (svc, store) = Build();

        var result = await svc.StartPairingAsync("Laptop");

        result.PairingId.Should().NotBeNullOrWhiteSpace();
        result.Code.Should().NotBeNullOrWhiteSpace();

        var pending = await store.GetPendingAsync(result.PairingId);
        pending.Should().NotBeNull();
        pending!.Approved.Should().BeFalse();
        pending.Code.Should().Be(result.Code);
        pending.DeviceName.Should().Be("Laptop");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task StartPairing_empty_name_throws(string? name)
    {
        var (svc, _) = Build();
        Func<Task> act = () => svc.StartPairingAsync(name!);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Approve_marks_pending_approved_and_creates_verifiable_device()
    {
        var (svc, store) = Build();
        var start = await svc.StartPairingAsync("Phone");

        var ok = await svc.ApproveAsync(start.Code);

        ok.Should().BeTrue();
        var pending = await store.GetPendingAsync(start.PairingId);
        pending!.Approved.Should().BeTrue();
        pending.ApprovedDeviceId.Should().NotBeNullOrWhiteSpace();
        pending.OneTimeApiKey.Should().NotBeNullOrWhiteSpace();

        var devices = await store.ListAsync();
        devices.Should().HaveCount(1);
        var device = devices.Single();
        device.Id.Should().Be(pending.ApprovedDeviceId);
        device.Name.Should().Be("Phone");

        // §A-3 — the issued key is "keyId.secret"; only the SECRET half is hashed and the public
        // keyId is stored unhashed for the indexed lookup.
        ApiKeyGenerator.TryParse(pending.OneTimeApiKey!, out var keyId, out var secret).Should().BeTrue();
        device.KeyId.Should().Be(keyId);
        ApiKeyHasher.Verify(secret, device.ApiKeyHash).Should().BeTrue();
        ApiKeyHasher.Verify(pending.OneTimeApiKey!, device.ApiKeyHash).Should().BeFalse(
            "the hash is of the secret half only, never the composite key");
    }

    [Fact]
    public async Task Approve_binds_device_to_the_approving_user_not_default()
    {
        // §A-2 fix: a device created at approval must carry the REAL approving user, never the
        // seeded default. Approve runs under the panel cookie, so the ambient user is the approver.
        var (svc, store) = Build(user: new FixedUser("real-user"));
        var start = await svc.StartPairingAsync("Phone");

        await svc.ApproveAsync(start.Code);

        var device = (await store.ListAsync()).Single();
        device.UserId.Should().Be("real-user");
    }

    // FIX A — pairing code entropy. The code must carry at least the new 40-bit floor (8 chars over a
    // 32-symbol alphabet), so an online attacker cannot feasibly guess a live code within the TTL.
    [Fact]
    public async Task StartPairing_code_meets_minimum_entropy()
    {
        var (svc, _) = Build();

        var start = await svc.StartPairingAsync("Laptop");

        // Length floor (8) and the per-char alphabet (32 -> 5 bits) give >= 40 bits.
        start.Code.Length.Should().BeGreaterThanOrEqualTo(8);
        DeviceService.CodeEntropyBits.Should().BeGreaterThanOrEqualTo(40);
        start.Code.Should().MatchRegex("^[0-9A-HJKMNP-TV-Z]{8,}$");
    }

    // FIX A — idempotent approve. Approving the same code twice must yield exactly ONE device and a
    // single live one-time key; the second call is a no-op success (no phantom device, no key
    // re-issue / overwrite).
    [Fact]
    public async Task Approve_twice_same_code_creates_one_device_and_is_idempotent()
    {
        var (svc, store) = Build(user: new FixedUser("real-user"));
        var start = await svc.StartPairingAsync("Phone");

        var first = await svc.ApproveAsync(start.Code);
        var pendingAfterFirst = await store.GetPendingAsync(start.PairingId);
        var firstKey = pendingAfterFirst!.OneTimeApiKey;
        var firstDeviceId = pendingAfterFirst.ApprovedDeviceId;

        var second = await svc.ApproveAsync(start.Code);

        first.Should().BeTrue();
        second.Should().BeTrue("a repeat approve is idempotent, not a failure");

        // Exactly one device — no phantom second DeviceRow.
        (await store.ListAsync()).Should().HaveCount(1);

        // The one-time key and approved device id were NOT overwritten by the second call.
        var pendingAfterSecond = await store.GetPendingAsync(start.PairingId);
        pendingAfterSecond!.OneTimeApiKey.Should().Be(firstKey);
        pendingAfterSecond.ApprovedDeviceId.Should().Be(firstDeviceId);
    }

    // FIX A — concurrent approve race. Two simultaneous approves of the same code must still leave
    // exactly one device and one live key (the atomic conditional claim serialises them).
    [Fact]
    public async Task Approve_concurrent_same_code_creates_one_device()
    {
        var (svc, store) = Build(user: new FixedUser("real-user"));
        var start = await svc.StartPairingAsync("Phone");

        var results = await Task.WhenAll(
            svc.ApproveAsync(start.Code),
            svc.ApproveAsync(start.Code),
            svc.ApproveAsync(start.Code));

        results.Should().OnlyContain(r => r == true);
        (await store.ListAsync()).Should().HaveCount(1, "the atomic claim admits exactly one winner");
    }

    // FIX A — expired code is rejected at approval. A pending row older than the TTL must not approve
    // and must not create a device.
    [Fact]
    public async Task Approve_expired_code_returns_false_and_creates_no_device()
    {
        var options = new ServerOptions { PendingPairingTtlMinutes = 15 };
        var (svc, store) = Build(user: new FixedUser("real-user"), options: options);

        // Seed a pending row created 20 minutes ago — past the 15-minute TTL.
        await store.SavePendingAsync(new PendingPairing
        {
            PairingId = "old-1",
            DeviceName = "Stale Phone",
            Code = "OLDCODE1",
            CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-20),
        });

        var ok = await svc.ApproveAsync("OLDCODE1");

        ok.Should().BeFalse();
        (await store.ListAsync()).Should().BeEmpty();
        // The row stays unapproved (the claim was rejected on the TTL predicate).
        (await store.GetPendingAsync("old-1"))!.Approved.Should().BeFalse();
    }

    // FIX A — a fresh code within the TTL still approves normally (guards against an off-by-one that
    // would reject live codes).
    [Fact]
    public async Task Approve_fresh_code_within_ttl_succeeds()
    {
        var options = new ServerOptions { PendingPairingTtlMinutes = 15 };
        var (svc, store) = Build(user: new FixedUser("real-user"), options: options);
        var start = await svc.StartPairingAsync("Phone");

        (await svc.ApproveAsync(start.Code)).Should().BeTrue();
        (await store.ListAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task Register_binds_device_to_token_user_and_sets_lease_and_key()
    {
        var options = new ServerOptions { DeviceLeaseTtlMinutes = 10 };
        var (svc, store) = Build(user: new FixedUser("token-user"), options: options);

        var before = DateTimeOffset.UtcNow;
        var result = await svc.RegisterAsync(new DeviceRegisterRequest(
            Name: "Workstation", Platform: "windows", HasOutlookCom: true, AppVersion: "1.2.3"));

        result.DeviceId.Should().NotBeNullOrWhiteSpace();
        result.ApiKey.Should().Contain(".");
        result.LeaseUntil.Should().BeOnOrAfter(before.AddMinutes(10).AddSeconds(-5));

        var device = (await store.ListAsync()).Single();
        device.UserId.Should().Be("token-user");
        device.Name.Should().Be("Workstation");
        device.Platform.Should().Be("windows");
        device.HasOutlookCom.Should().BeTrue();
        device.AppVersion.Should().Be("1.2.3");
        device.LeaseUntil.Should().NotBeNull();

        // The returned key authenticates: keyId stored unhashed, secret verifies against the hash.
        ApiKeyGenerator.TryParse(result.ApiKey, out var keyId, out var secret).Should().BeTrue();
        device.KeyId.Should().Be(keyId);
        ApiKeyHasher.Verify(secret, device.ApiKeyHash).Should().BeTrue();
    }

    [Fact]
    public async Task Register_with_blank_name_generates_geek_name_from_account_email()
    {
        var users = new StubUserStore(new ZyncMaster.Server.Data.UserRow
        {
            Id = "u-gen", PrimaryEmail = "zezelazo@msn.com",
        });
        var (svc, store) = Build(user: new FixedUser("u-gen"), users: users);

        await svc.RegisterAsync(new DeviceRegisterRequest(Name: ""));

        var device = (await store.ListAsync()).Single();
        device.Name.Should().NotBeNullOrWhiteSpace();
        device.Name.Should().NotBe("Device");
        device.Name.Should().EndWith("-zezelazo");
    }

    [Fact]
    public async Task Register_twice_without_name_yields_distinct_names_for_same_user()
    {
        var users = new StubUserStore(new ZyncMaster.Server.Data.UserRow
        {
            Id = "u-two", PrimaryEmail = "dupe@example.com",
        });
        var (svc, store) = Build(user: new FixedUser("u-two"), users: users);

        await svc.RegisterAsync(new DeviceRegisterRequest(Name: null!));
        await svc.RegisterAsync(new DeviceRegisterRequest(Name: "   "));

        var names = (await store.ListAsync()).Select(d => d.Name).ToList();
        names.Should().HaveCount(2);
        names.Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(2);
    }

    [Fact]
    public async Task GetMe_backfills_name_for_nameless_device_and_persists()
    {
        var users = new StubUserStore(new ZyncMaster.Server.Data.UserRow
        {
            Id = "u-heal", PrimaryEmail = "healme@example.com",
        });
        var (svc, store) = Build(user: new FixedUser("u-heal"), users: users);

        // Seed a legacy device with a blank name directly in the store.
        var legacy = new Device
        {
            Id = "legacy-1", UserId = "u-heal", Name = "", ApiKeyHash = "h",
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        await store.AddAsync(legacy);

        var result = await svc.GetMeAsync("legacy-1");

        result.Should().NotBeNull();
        result!.Name.Should().NotBeNullOrWhiteSpace();
        result.Name.Should().EndWith("-healme");

        // Persisted, not just returned.
        var stored = await store.GetAsync("legacy-1");
        stored!.Name.Should().Be(result.Name);
    }

    [Fact]
    public async Task GetMe_leaves_existing_name_untouched()
    {
        var (svc, store) = Build();
        var device = new Device
        {
            Id = "named-1", UserId = DefaultCurrentUserAccessor.DefaultUserId, Name = "Keep Me",
            ApiKeyHash = "h", CreatedUtc = DateTimeOffset.UtcNow,
        };
        await store.AddAsync(device);

        var result = await svc.GetMeAsync("named-1");

        result!.Name.Should().Be("Keep Me");
    }

    [Fact]
    public async Task Register_normalizes_unknown_platform_to_windows()
    {
        var (svc, store) = Build();
        await svc.RegisterAsync(new DeviceRegisterRequest(Name: "X", Platform: "android"));
        (await store.ListAsync()).Single().Platform.Should().Be("windows");
    }

    // Idempotent re-registration by name — the fix for "no device key present". When the App
    // re-registers (its local key was lost or never persisted, e.g. a name already created by an
    // earlier pairing) with the SAME explicit name, the existing device is RE-KEYED in place: same
    // stable deviceId, a fresh working key, exactly ONE device — never a unique-index failure that
    // would leave the device permanently keyless.
    [Fact]
    public async Task Register_with_name_already_owned_rekeys_same_device_in_place()
    {
        var (svc, store) = Build(user: new FixedUser("token-user"));

        var first = await svc.RegisterAsync(new DeviceRegisterRequest(
            Name: "DESKTOP-PC", Platform: "windows", HasOutlookCom: false, AppVersion: "0.2.5"));
        var second = await svc.RegisterAsync(new DeviceRegisterRequest(
            Name: "DESKTOP-PC", Platform: "windows", HasOutlookCom: true, AppVersion: "0.2.6"));

        // Same logical device — stable id, not a duplicate row.
        second.DeviceId.Should().Be(first.DeviceId);
        (await store.ListAsync()).Should().HaveCount(1, "re-registration re-keys in place, it never spawns a duplicate");

        // A fresh, DIFFERENT, working key was issued; the old key no longer authenticates.
        second.ApiKey.Should().NotBe(first.ApiKey);
        var device = (await store.ListAsync()).Single();
        ApiKeyGenerator.TryParse(second.ApiKey, out var keyId2, out var secret2).Should().BeTrue();
        device.KeyId.Should().Be(keyId2);
        ApiKeyHasher.Verify(secret2, device.ApiKeyHash).Should().BeTrue("the new key authenticates");
        ApiKeyGenerator.TryParse(first.ApiKey, out _, out var secret1).Should().BeTrue();
        ApiKeyHasher.Verify(secret1, device.ApiKeyHash).Should().BeFalse("the old key was rotated out");

        // Capability flags + lease refreshed from the latest registration.
        device.HasOutlookCom.Should().BeTrue();
        device.AppVersion.Should().Be("0.2.6");
        device.LeaseUntil.Should().NotBeNull();
    }

    // Re-registration matches the existing name case-insensitively (one machine == one name), so a
    // trim/casing difference re-keys the same device rather than spawning a duplicate.
    [Fact]
    public async Task Register_with_name_match_is_case_insensitive_rekeys_same_device()
    {
        var (svc, store) = Build(user: new FixedUser("token-user"));

        var first = await svc.RegisterAsync(new DeviceRegisterRequest(Name: "Workstation"));
        var second = await svc.RegisterAsync(new DeviceRegisterRequest(Name: "  workstation  "));

        second.DeviceId.Should().Be(first.DeviceId);
        (await store.ListAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task Heartbeat_renews_lease_for_known_device()
    {
        var options = new ServerOptions { DeviceLeaseTtlMinutes = 10 };
        var (svc, store) = Build(options: options);
        var reg = await svc.RegisterAsync(new DeviceRegisterRequest(Name: "X"));

        var before = DateTimeOffset.UtcNow;
        var result = await svc.HeartbeatAsync(reg.DeviceId);

        result.Should().NotBeNull();
        result!.LeaseUntil.Should().BeOnOrAfter(before.AddMinutes(10).AddSeconds(-5));
        (await store.GetAsync(reg.DeviceId))!.LeaseUntil.Should().Be(result.LeaseUntil);
    }

    [Fact]
    public async Task Heartbeat_unknown_device_returns_null()
    {
        var (svc, _) = Build();
        (await svc.HeartbeatAsync("nope")).Should().BeNull();
    }

    [Fact]
    public async Task Approve_unknown_code_returns_false()
    {
        var (svc, _) = Build();
        (await svc.ApproveAsync("ZZZZZZ")).Should().BeFalse();
    }

    [Fact]
    public async Task CompletePairing_before_approval_returns_not_approved()
    {
        var (svc, _) = Build();
        var start = await svc.StartPairingAsync("Laptop");

        var result = await svc.CompletePairingAsync(start.PairingId);

        result.Approved.Should().BeFalse();
        result.ApiKey.Should().BeNull();
    }

    [Fact]
    public async Task CompletePairing_after_approval_returns_key_once()
    {
        var (svc, _) = Build();
        var start = await svc.StartPairingAsync("Laptop");
        await svc.ApproveAsync(start.Code);

        // FIX 1 — the PKCE verifier minted by start must be presented to claim the key.
        var first = await svc.CompletePairingAsync(start.PairingId, start.Verifier);
        first.Approved.Should().BeTrue();
        first.ApiKey.Should().NotBeNullOrWhiteSpace();
        first.DeviceId.Should().NotBeNullOrWhiteSpace();

        var second = await svc.CompletePairingAsync(start.PairingId, start.Verifier);
        second.Approved.Should().BeTrue();
        second.ApiKey.Should().BeNull();
    }

    // FIX 1 — a complete that presents the WRONG verifier (the attack: a third party who knows the
    // pairingId but not the initiator's verifier) does NOT get the api key, even after approval.
    [Fact]
    public async Task CompletePairing_with_wrong_verifier_is_not_approved_and_yields_no_key()
    {
        var (svc, _) = Build();
        var start = await svc.StartPairingAsync("Laptop");
        await svc.ApproveAsync(start.Code);

        var result = await svc.CompletePairingAsync(start.PairingId, "not-the-real-verifier");

        result.Approved.Should().BeFalse();
        result.ApiKey.Should().BeNull();
    }

    // FIX 1 — a complete that presents NO verifier (the legacy/anonymous attacker shape) is rejected
    // for a row minted with a verifier hash.
    [Fact]
    public async Task CompletePairing_with_missing_verifier_is_not_approved()
    {
        var (svc, _) = Build();
        var start = await svc.StartPairingAsync("Laptop");
        await svc.ApproveAsync(start.Code);

        var result = await svc.CompletePairingAsync(start.PairingId, verifier: null);

        result.Approved.Should().BeFalse();
        result.ApiKey.Should().BeNull();
    }

    [Fact]
    public async Task CompletePairing_unknown_pairing_returns_not_approved_without_throw()
    {
        var (svc, _) = Build();
        var result = await svc.CompletePairingAsync("does-not-exist");
        result.Approved.Should().BeFalse();
        result.ApiKey.Should().BeNull();
    }

    [Fact]
    public async Task IsNameAvailable_true_when_free_false_when_taken_case_insensitive()
    {
        var (svc, store) = Build();
        await store.AddAsync(new Device
        {
            Id = "d1", UserId = DefaultCurrentUserAccessor.DefaultUserId, Name = "Frodo",
            ApiKeyHash = "h", CreatedUtc = DateTimeOffset.UtcNow,
        });

        (await svc.IsNameAvailableAsync("Gandalf", excludeDeviceId: null)).Should().BeTrue();
        (await svc.IsNameAvailableAsync("frodo", excludeDeviceId: null)).Should().BeFalse("case-insensitive");
        (await svc.IsNameAvailableAsync("  FRODO  ", excludeDeviceId: null)).Should().BeFalse("trim + ci");
    }

    [Fact]
    public async Task IsNameAvailable_excludes_the_callers_own_device()
    {
        var (svc, store) = Build();
        await store.AddAsync(new Device
        {
            Id = "self", UserId = DefaultCurrentUserAccessor.DefaultUserId, Name = "Keeper",
            ApiKeyHash = "h", CreatedUtc = DateTimeOffset.UtcNow,
        });

        // Re-typing the device's own name is available when that device is excluded.
        (await svc.IsNameAvailableAsync("Keeper", excludeDeviceId: "self")).Should().BeTrue();
        (await svc.IsNameAvailableAsync("Keeper", excludeDeviceId: null)).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task IsNameAvailable_false_for_blank(string name)
    {
        var (svc, _) = Build();
        (await svc.IsNameAvailableAsync(name, excludeDeviceId: null)).Should().BeFalse();
    }

    [Fact]
    public async Task IsNameAvailable_false_for_too_long()
    {
        var (svc, _) = Build();
        (await svc.IsNameAvailableAsync(new string('x', 101), excludeDeviceId: null)).Should().BeFalse();
    }

    [Fact]
    public async Task Rename_to_name_used_by_another_device_throws_name_taken()
    {
        var (svc, store) = Build();
        await store.AddAsync(new Device
        {
            Id = "a", UserId = DefaultCurrentUserAccessor.DefaultUserId, Name = "Device A",
            ApiKeyHash = "h", CreatedUtc = DateTimeOffset.UtcNow,
        });
        await store.AddAsync(new Device
        {
            Id = "b", UserId = DefaultCurrentUserAccessor.DefaultUserId, Name = "Device B",
            ApiKeyHash = "h", CreatedUtc = DateTimeOffset.UtcNow,
        });

        Func<Task> act = () => svc.RenameAsync("a", "device b");
        await act.Should().ThrowAsync<DeviceNameTakenException>();
    }

    [Fact]
    public async Task Rename_to_own_current_name_succeeds()
    {
        var (svc, store) = Build();
        await store.AddAsync(new Device
        {
            Id = "a", UserId = DefaultCurrentUserAccessor.DefaultUserId, Name = "Mine",
            ApiKeyHash = "h", CreatedUtc = DateTimeOffset.UtcNow,
        });

        var result = await svc.RenameAsync("a", "Mine");

        result.Should().NotBeNull();
        result!.Name.Should().Be("Mine");
    }

    // Track B — IsDeviceOnlineAsync: a live lease (LeaseUntil in the future) means the App is running.
    [Fact]
    public async Task IsDeviceOnline_true_when_lease_is_live()
    {
        var (svc, store) = Build();
        await store.AddAsync(new Device
        {
            Id = "d1", UserId = DefaultCurrentUserAccessor.DefaultUserId, Name = "Laptop",
            ApiKeyHash = "h", CreatedUtc = DateTimeOffset.UtcNow,
            LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(5),
        });

        (await svc.IsDeviceOnlineAsync("d1")).Should().BeTrue();
    }

    // Track B — an expired lease (LeaseUntil in the past) means the App is no longer running: offline.
    [Fact]
    public async Task IsDeviceOnline_false_when_lease_expired()
    {
        var (svc, store) = Build();
        await store.AddAsync(new Device
        {
            Id = "d1", UserId = DefaultCurrentUserAccessor.DefaultUserId, Name = "Laptop",
            ApiKeyHash = "h", CreatedUtc = DateTimeOffset.UtcNow,
            LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(-1),
        });

        (await svc.IsDeviceOnlineAsync("d1")).Should().BeFalse();
    }

    // Track B — a device that never claimed a lease (null) is offline.
    [Fact]
    public async Task IsDeviceOnline_false_when_no_lease()
    {
        var (svc, store) = Build();
        await store.AddAsync(new Device
        {
            Id = "d1", UserId = DefaultCurrentUserAccessor.DefaultUserId, Name = "Laptop",
            ApiKeyHash = "h", CreatedUtc = DateTimeOffset.UtcNow,
        });

        (await svc.IsDeviceOnlineAsync("d1")).Should().BeFalse();
    }

    // Track B — an unknown / blank device id is offline (never throws).
    [Fact]
    public async Task IsDeviceOnline_false_for_unknown_or_blank_id()
    {
        var (svc, _) = Build();

        (await svc.IsDeviceOnlineAsync("missing")).Should().BeFalse();
        (await svc.IsDeviceOnlineAsync("")).Should().BeFalse();
    }
}
