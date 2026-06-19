# Implementation plan — per-account clipboard retention + periodic purge

> **For the executor (you, even if you are Haiku): follow the phases IN ORDER. Do not skip the build/test
> gates. Every code block says the EXACT file and the EXACT anchor text to find. Match indentation. If an
> anchor is not found verbatim, STOP and re-read the file — do not guess.**

## 0. Context you need (read once)

Zync Master = .NET 10 server (`src/ZyncMaster.Server`) + desktop App + vanilla-JS panel (`ui/`). The
clipboard syncs text/image/file records through the server; they pile up in the `ClipboardItems` table and
(for files) as blobs on disk. There is ALREADY a 24h global retention (evicts on each new item) and a
background `EphemeralPurgeService` that runs every 6h. This plan makes the retention window **configurable
per user** (default 24h) and adds a **periodic purge** so old records are deleted even for users who stop
copying — piggy-backing on the existing 6h sweep so nothing new has to be scheduled.

Key existing files:
- `src/ZyncMaster.Server/Data/Entities.cs` — `UserRow` (the user table row).
- `src/ZyncMaster.Server/Modules/Clipboard/ClipboardOptions.cs` — `RetentionMaxAge` (default 24h).
- `src/ZyncMaster.Server/Modules/Clipboard/EfClipboardHistoryStore.cs` — append-time eviction + blob cleanup.
- `src/ZyncMaster.Server/Infrastructure/EphemeralPurgeService.cs` — the 6h background sweep (`PurgeOnceAsync`).
- `src/ZyncMaster.Server/Modules/Clipboard/ClipboardEndpoints.cs` — the REST surface.
- `src/ZyncMaster.Server/Modules/Clipboard/IClipboardBlobStore.cs` — blob delete.
- `ui/js/views/clipboard.js` — the in-app clipboard settings screen (`renderClipboardSettings`).

Bounds decision (use these everywhere): retention is an **int hours**, **min 1, max 720 (30 days)**, or
**null = use the server default (24h)**. The UI offers presets 1h / 6h / 24h / 72h / 168h(7d) but the API
accepts any int in `[1,720]` or null.

Build/test commands (run from repo root `C:\Code\SyncMaster`):
- Build: `dotnet build -c Release --nologo -m:1`
- Server tests: `dotnet test tests/ZyncMaster.Server.Tests/ZyncMaster.Server.Tests.csproj -c Release --nologo -m:1 --filter "FullyQualifiedName~Clipboard|FullyQualifiedName~Purge"`
- JS check: `node --check ui/js/views/clipboard.js`
- **Rule: 0 warnings, 0 failed tests before each commit.**

---

## Phase 1 — Data model: per-user retention column

**1.1** Edit `src/ZyncMaster.Server/Data/Entities.cs`. Find this anchor:
```csharp
    // Subscription plan slug; null means "everything unlocked" (no plan gating).
    public string? Plan { get; set; }
}
```
Replace it with:
```csharp
    // Subscription plan slug; null means "everything unlocked" (no plan gating).
    public string? Plan { get; set; }

    // Per-account clipboard retention window, in HOURS. null = use the server default
    // (ClipboardOptions.RetentionMaxAge, 24h). Clipboard records older than this are evicted on append
    // AND swept periodically by EphemeralPurgeService. Range enforced by the API: 1..720 (30 days).
    public int? ClipboardRetentionHours { get; set; }
}
```

**1.2** Create the EF migration. Run from the repo root:
```
dotnet ef migrations add ClipboardRetentionPerUser --project src/ZyncMaster.Server --startup-project src/ZyncMaster.Server
```
If `dotnet ef` is missing: `dotnet tool install --global dotnet-ef` first. This generates a new file under
`src/ZyncMaster.Server/Data/Migrations/` adding the `ClipboardRetentionHours` column (nullable integer).
Open the generated `*_ClipboardRetentionPerUser.cs` and confirm `Up()` does `AddColumn<int>(... nullable: true ...)`
on the `Users` table and `Down()` drops it. Do not edit it further.

**1.3 GATE:** `dotnet build -c Release --nologo -m:1` → must be 0 errors.

---

## Phase 2 — Append-time eviction uses the per-user window

**2.1** Edit `src/ZyncMaster.Server/Modules/Clipboard/EfClipboardHistoryStore.cs`. Find this anchor (inside
`AppendAsync`, the FIFO+age eviction block):
```csharp
        var stale = new HashSet<string>(byNewest.Skip(_opts.MaxItemsPerUser).Select(x => x.Id));
        if (_opts.RetentionMaxAge > TimeSpan.Zero)
        {
            var cutoff = DateTimeOffset.UtcNow - _opts.RetentionMaxAge;
```
Replace those three lines with:
```csharp
        var stale = new HashSet<string>(byNewest.Skip(_opts.MaxItemsPerUser).Select(x => x.Id));
        // Per-account retention: this user's ClipboardRetentionHours (if set) overrides the server
        // default. null => the global RetentionMaxAge. Resolved from the same DbContext, no extra round-trip cost.
        var userHours = await db.Users
            .Where(u => u.Id == _user.UserId)
            .Select(u => u.ClipboardRetentionHours)
            .FirstOrDefaultAsync(ct);
        var window = userHours is int h && h > 0 ? TimeSpan.FromHours(h) : _opts.RetentionMaxAge;
        if (window > TimeSpan.Zero)
        {
            var cutoff = DateTimeOffset.UtcNow - window;
```
(The rest of the block — the `foreach` adding aged ids to `stale`, the `ExecuteDeleteAsync`, and the File
blob deletion — stays exactly as is.)

**2.2 GATE:** `dotnet build -c Release --nologo -m:1` → 0 errors.

---

## Phase 3 — Periodic purge (the cron) in EphemeralPurgeService

The sweep already runs every `ServerOptions.EphemeralPurgeIntervalHours` (default **6h**). We add a clipboard
pass to it. It needs the blob store + the clipboard default window, so we inject them.

**3.1** Edit `src/ZyncMaster.Server/Infrastructure/EphemeralPurgeService.cs`. Find the constructor + fields:
```csharp
    private readonly IDbContextFactory<ZyncMasterDbContext> _factory;
    private readonly ILogger<EphemeralPurgeService> _logger;
    private readonly TimeSpan _interval;
    private readonly int _pendingPairingTtlMinutes;

    public EphemeralPurgeService(
        IDbContextFactory<ZyncMasterDbContext> factory,
        ILogger<EphemeralPurgeService> logger,
        IOptions<ServerOptions>? options = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var hours = options?.Value.EphemeralPurgeIntervalHours ?? 6;
        _interval = TimeSpan.FromHours(hours <= 0 ? 6 : hours);
        var ttl = options?.Value.PendingPairingTtlMinutes ?? 15;
        _pendingPairingTtlMinutes = ttl <= 0 ? 15 : ttl;
    }
```
Replace with (adds the blob store + the clipboard default, both optional so existing tests that build the
service without them keep compiling):
```csharp
    private readonly IDbContextFactory<ZyncMasterDbContext> _factory;
    private readonly ILogger<EphemeralPurgeService> _logger;
    private readonly TimeSpan _interval;
    private readonly int _pendingPairingTtlMinutes;
    private readonly IClipboardBlobStore? _blobs;
    private readonly TimeSpan _clipboardDefaultWindow;

    public EphemeralPurgeService(
        IDbContextFactory<ZyncMasterDbContext> factory,
        ILogger<EphemeralPurgeService> logger,
        IOptions<ServerOptions>? options = null,
        IClipboardBlobStore? blobs = null,
        IOptions<ClipboardOptions>? clipboardOptions = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var hours = options?.Value.EphemeralPurgeIntervalHours ?? 6;
        _interval = TimeSpan.FromHours(hours <= 0 ? 6 : hours);
        var ttl = options?.Value.PendingPairingTtlMinutes ?? 15;
        _pendingPairingTtlMinutes = ttl <= 0 ? 15 : ttl;
        _blobs = blobs;
        _clipboardDefaultWindow = clipboardOptions?.Value.RetentionMaxAge ?? TimeSpan.FromHours(24);
    }
```

**3.2** In the same file, find the END of `PurgeOnceAsync` — the `return` that sums the deleted rows. It
looks like `return access + refresh + magic + locks + pairings;` (the exact names may differ; it is the
final `return <sum>;` of `PurgeOnceAsync`). Insert a clipboard purge call right BEFORE that return, and add
the total. Change:
```csharp
        return access + refresh + magic + locks + pairings;
    }
```
to:
```csharp
        var clipboard = await PurgeClipboardAsync(now, db, ct).ConfigureAwait(false);

        return access + refresh + magic + locks + pairings + clipboard;
    }

    // Deletes clipboard records older than each user's retention window (their ClipboardRetentionHours, or
    // the server default), and removes the on-disk blob of every deleted File. Runs on the same 6h sweep,
    // so old records are purged even for users who stopped copying (append-time eviction alone would never
    // fire for them). The slim projection is bounded by the per-user FIFO cap, so materialising it is cheap.
    private async Task<int> PurgeClipboardAsync(DateTimeOffset now, ZyncMasterDbContext db, CancellationToken ct)
    {
        // userId -> window (hours override, else the server default).
        var users = await db.Users
            .Select(u => new { u.Id, u.ClipboardRetentionHours })
            .ToListAsync(ct).ConfigureAwait(false);
        var windowByUser = users.ToDictionary(
            u => u.Id,
            u => u.ClipboardRetentionHours is int h && h > 0 ? TimeSpan.FromHours(h) : _clipboardDefaultWindow);

        // Slim projection (no payloads). DateTimeOffset compare done in memory — SQLite cannot translate it.
        var items = await db.ClipboardItems
            .Select(x => new { x.Id, x.UserId, x.Type, x.CreatedUtc })
            .ToListAsync(ct).ConfigureAwait(false);

        var staleIds = new List<string>();
        var staleFileByUser = new List<(string UserId, string Id)>();
        foreach (var it in items)
        {
            var window = windowByUser.TryGetValue(it.UserId, out var w) ? w : _clipboardDefaultWindow;
            if (window <= TimeSpan.Zero) continue;          // 0/negative disables age purge for that user
            if (it.CreatedUtc >= now - window) continue;     // still inside the window
            staleIds.Add(it.Id);
            if (it.Type == nameof(ClipboardItemType.File))
                staleFileByUser.Add((it.UserId, it.Id));
        }

        if (staleIds.Count == 0) return 0;

        await db.ClipboardItems.Where(x => staleIds.Contains(x.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);

        // Best-effort blob cleanup for the deleted files (a missing blob is a no-op in the store).
        if (_blobs is not null)
            foreach (var f in staleFileByUser)
                await _blobs.DeleteAsync(f.UserId, f.Id, ct).ConfigureAwait(false);

        return staleIds.Count;
    }
```
> Note: `ClipboardItemType`, `IClipboardBlobStore`, `ClipboardOptions` are all in the `ZyncMaster.Server`
> namespace (same as this file's `namespace ZyncMaster.Server;`), so no new `using` is needed.

**3.3** DI: the blob store + clipboard options are already registered (`Program.cs` registers
`IClipboardBlobStore` and `Configure<ClipboardOptions>`), so the new optional constructor args resolve
automatically. No Program.cs change required for Phase 3.

**3.4 GATE:** `dotnet build -c Release --nologo -m:1` → 0 errors.

---

## Phase 4 — API endpoint to get/set the per-account window

**4.1** Edit `src/ZyncMaster.Server/Modules/Clipboard/ClipboardEndpoints.cs`. Find the GET history endpoint
anchor (near the top of `MapClipboardEndpoints`):
```csharp
        }).RequireCookieOrApiKeyOrIdentityBearer();

        // POST publish — validate, decode, append (user-scoped), then fan out to the user's OTHER
```
Insert these two endpoints BETWEEN them (right after the history GET's `.RequireCookieOrApiKeyOrIdentityBearer();`):
```csharp

        // GET the caller's clipboard retention window (hours), or null when unset (server default applies).
        app.MapGet("/api/clipboard/retention", async (
            IDbContextFactory<ZyncMasterDbContext> dbf,
            ICurrentUserAccessor currentUser,
            CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var hours = await db.Users.Where(u => u.Id == currentUser.UserId)
                .Select(u => u.ClipboardRetentionHours).FirstOrDefaultAsync(ct);
            return Results.Ok(new { hours });
        }).RequireCookieOrApiKeyOrIdentityBearer();

        // PUT the caller's clipboard retention window. Body: { "hours": <1..720 | null> }. null clears the
        // override (server default applies). Out-of-range is 400.
        app.MapPut("/api/clipboard/retention", async (
            SetRetentionRequest req,
            IDbContextFactory<ZyncMasterDbContext> dbf,
            ICurrentUserAccessor currentUser,
            CancellationToken ct) =>
        {
            if (req.Hours is int h && (h < 1 || h > 720))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["hours"] = new[] { "Retention must be between 1 and 720 hours, or null for the default." },
                });
            await using var db = await dbf.CreateDbContextAsync(ct);
            var rows = await db.Users.Where(u => u.Id == currentUser.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.ClipboardRetentionHours, req.Hours), ct);
            return rows > 0 ? Results.Ok(new { hours = req.Hours }) : Results.NotFound();
        }).RequireCookieOrApiKeyOrIdentityBearer();
```

**4.2** Add the request DTO. Edit `src/ZyncMaster.Server/Modules/Clipboard/ClipboardRequests.cs`. At the end
of the file (after the last class/record, before the final closing of the namespace if any — this file uses
file-scoped namespace, so just append at the end), add:
```csharp

// Body for PUT /api/clipboard/retention. Hours null = clear the override (use the server default).
public sealed record SetRetentionRequest(int? Hours);
```

**4.3** This file needs the EF + DbContext types in scope. `ClipboardEndpoints.cs` already lives in
`namespace ZyncMaster.Server;`. Confirm the top of `ClipboardEndpoints.cs` has `using Microsoft.EntityFrameworkCore;`
and `using ZyncMaster.Server.Data;`. If EITHER is missing, add it at the top of the file (the other clipboard
files import them; `ExecuteUpdateAsync`/`FirstOrDefaultAsync`/`CreateDbContextAsync` need EF Core, and
`ZyncMasterDbContext` needs `.Data`).

**4.4 GATE:** `dotnet build -c Release --nologo -m:1` → 0 errors.

---

## Phase 5 — UI: a retention selector in Clipboard → Settings

**5.1** Edit `ui/js/views/clipboard.js`. Find this anchor inside `renderClipboardSettings` (the "this device"
card rows), specifically the last row of that `cfgSection`:
```javascript
        cfgRow('Show shortcut hints',
          el('div', { class: 'cfg-row__hint', text: 'Key bar at the foot of the viewer (Rich only)' }),
          clipToggle(me, 'showHints', { label: 'Show shortcut hints' }))));
```
Replace it with (adds a retention row after the hints row, still inside the same `cfgSection(...)` call):
```javascript
        cfgRow('Show shortcut hints',
          el('div', { class: 'cfg-row__hint', text: 'Key bar at the foot of the viewer (Rich only)' }),
          clipToggle(me, 'showHints', { label: 'Show shortcut hints' })),
        cfgRow('Keep clipboard for',
          el('div', { class: 'cfg-row__hint', text: 'Records older than this are deleted automatically' }),
          retentionSelect())));
```

**5.2** In the same file, add the `retentionSelect` helper + its state. Find the anchor at the top of the
module's settings section (just before `function renderClipboardSettings(root) {`):
```javascript
  function renderClipboardSettings(root) {
```
Insert BEFORE it:
```javascript
  // Per-account retention window (hours). null = server default (24h). Loaded lazily from the server.
  let clipRetentionHours = null;
  let clipRetentionLoaded = false;
  const CLIP_RETENTION_PRESETS = [[1, '1 hour'], [6, '6 hours'], [24, '24 hours (default)'], [72, '3 days'], [168, '7 days']];

  function retentionSelect() {
    if (!clipRetentionLoaded && Bridge.available) {
      clipRetentionLoaded = true;
      Bridge.call('getClipboardRetention')
        .then((r) => { clipRetentionHours = (r && typeof r.hours === 'number') ? r.hours : null; softRepaint(); })
        .catch(() => {});
    }
    const sel = el('select', { class: 'cfg-select', 'aria-label': 'Clipboard retention' });
    CLIP_RETENTION_PRESETS.forEach(([h, label]) => {
      const opt = el('option', { value: String(h), text: label });
      if ((clipRetentionHours ?? 24) === h) opt.selected = true;
      sel.append(opt);
    });
    sel.addEventListener('change', () => {
      const hours = parseInt(sel.value, 10);
      clipRetentionHours = hours;
      if (Bridge.available) {
        Bridge.call('setClipboardRetention', hours)
          .then(() => announce('Retention updated'))
          .catch(() => announce('Could not update retention'));
      }
    });
    return sel;
  }
```
> If `cfg-select` has no CSS, the native select still works (acceptable). A nicer style can be added later to
> `ui/css/clipboard.css` but is NOT required for this plan.

**5.3** Wire the two bridge calls. The desktop App's bridge must forward `getClipboardRetention` /
`setClipboardRetention` to the server endpoints from Phase 4. Edit
`src/ZyncMaster.App/Bridge/UiBridge.cs`. Find the `case "saveClipboardFile":` block (added in v0.4.7) and
add two cases right before it:
```csharp
            case "getClipboardRetention":
            {
                var hours = await _engine.GetClipboardRetentionAsync(ct);
                return JsonSerializer.Serialize(new { hours }, JsonOptions);
            }
            case "setClipboardRetention":
            {
                // Payload is the hours as a bare number (or null).
                int? hours = int.TryParse(UnwrapString(message.Payload), out var hv) ? hv : (int?)null;
                await _engine.SetClipboardRetentionAsync(hours, ct);
                return JsonSerializer.Serialize(new { ok = true }, JsonOptions);
            }
```
**5.4** Add the two methods to the engine actions. This requires: a declaration on `IEngineActions.cs`, a
no-op on `UnconfiguredEngineActions.cs`, and the real impl on `EngineActions.cs` that calls the server
through the clipboard transport's HTTP path (`GET/PUT /api/clipboard/retention`).
- `src/ZyncMaster.App/Bridge/IEngineActions.cs` — after `Task<string?> SaveClipboardFileAsync(...)`, add:
  ```csharp
  Task<int?> GetClipboardRetentionAsync(CancellationToken ct = default);
  Task SetClipboardRetentionAsync(int? hours, CancellationToken ct = default);
  ```
- `src/ZyncMaster.App/Bridge/UnconfiguredEngineActions.cs` — add:
  ```csharp
  public Task<int?> GetClipboardRetentionAsync(CancellationToken ct = default) => Task.FromResult<int?>(null);
  public Task SetClipboardRetentionAsync(int? hours, CancellationToken ct = default) => Task.CompletedTask;
  ```
- `src/ZyncMaster.App/Bridge/EngineActions.cs` — add a real impl. The simplest correct approach: extend
  `IClipboardTransport` (`src/ZyncMaster.Engine/Clipboard/IClipboardTransport.cs`) with
  `Task<int?> GetRetentionAsync(ct)` + `Task SetRetentionAsync(int? hours, ct)`, implement them in
  `HttpWsClipboardTransport.cs` (GET/PUT `/api/clipboard/retention`, same `SendAsync` JSON helper used by
  the other calls), add no-op overrides to the THREE test fakes that implement `IClipboardTransport`
  (`tests/ZyncMaster.Engine.Tests/Clipboard/ClipboardServiceTests.cs`,
  `ClipboardKeyExchangeTests.cs`, `ClipboardKeyAdmissionTests.cs`), and have `EngineActions` delegate to the
  transport. (Pattern: mirror how `DeleteEntryAsync` / `GetSettingsAsync` flow from EngineActions →
  transport.) Build will name every fake that still needs the methods — add them until it is 0 errors.

**5.5 GATE:** `node --check ui/js/views/clipboard.js` (OK) AND `dotnet build -c Release --nologo -m:1` (0 errors).

---

## Phase 6 — Tests (write them; do not skip)

Add to `tests/ZyncMaster.Server.Tests/Clipboard/`:

**6.1** In `ClipboardHistoryStoreTests.cs` — append-time per-user window. NOTE: the existing harness seeds no
`UserRow`, and the new code reads `db.Users`. Add a test that seeds a user with a 1h window and asserts an
item 2h old is evicted on the next append, while the same item under a 48h window survives. If the harness
cannot seed a `UserRow` easily, assert via the default path (no user row → `ClipboardRetentionHours` lookup
returns null → falls back to `_opts.RetentionMaxAge`, which the existing aged-eviction tests already cover) —
and put the per-user-window assertion in the `ClipboardEndpointsTests` integration test (6.3) instead, where
a real user exists.

**6.2** New file `tests/ZyncMaster.Server.Tests/Clipboard/ClipboardPurgeTests.cs` — drive
`EphemeralPurgeService.PurgeOnceAsync(now)` directly (it is public + clock-parameterised). Build the service
over the SQLite `ServerTestFactory` DbContext factory + a temp `DiskClipboardBlobStore`, seed: a user with
`ClipboardRetentionHours = 1`, two clipboard items (one 2h old → must be purged, one 10min old → must
survive) and a File item 2h old with a blob in the store. Call `PurgeOnceAsync(now)`; assert the old items
are gone, the fresh one remains, and the File's blob was deleted from the store. Add a second test: a user
with `ClipboardRetentionHours = null` falls back to the 24h default (a 30h-old item is purged, a 2h-old one
survives).

**6.3** In `ClipboardEndpointsTests.cs` (the real-Program integration harness) — add:
- `Retention_get_defaults_to_null` (a fresh user → GET returns `{hours:null}`).
- `Retention_put_then_get_roundtrips` (PUT `{hours:6}` → 200; GET → `{hours:6}`).
- `Retention_put_out_of_range_returns_400` (PUT `{hours:0}` and `{hours:1000}` → 400).

**6.4 GATE:** server tests green:
```
dotnet test tests/ZyncMaster.Server.Tests/ZyncMaster.Server.Tests.csproj -c Release --nologo -m:1 --filter "FullyQualifiedName~Clipboard|FullyQualifiedName~Purge"
```
Then the FULL suite once: `dotnet test -c Release --nologo -m:1`. Must be 0 warnings, 0 failed.

---

## Phase 7 — Version, commit, deploy

**7.1** Bump the app version (one place each): `src/ZyncMaster.App/ZyncMaster.App.csproj` `<Version>`/
`<InformationalVersion>` and `ui/js/app.js` `const VERSION` → the next patch (e.g. `0.4.8`).

**7.2** Commit with EXPLICIT paths (NEVER `git add -A`, NEVER `git add ... 2>/dev/null` — a bad path silently
stages nothing). After staging, run `git diff --cached --name-only` and confirm every file is listed. Suggested
atomic commits: (a) the migration + entity + store + purge (server), (b) the endpoint + DTO, (c) the bridge +
UI, (d) the version bump. **No AI attribution in any commit message.**

**7.3** Push `main`. Deploy the server (the migration applies via the efbundle in the deploy):
```
gh workflow run deploy-syncmaster-vps.yml --ref main
```
Watch it: `gh run watch <id> --exit-status`. Then release the app:
```
gh workflow run release-app.yml --ref main -f version=0.4.8
```

**7.4 VERIFY LIVE** (these are the real "done" checks):
- `curl -s -o /dev/null -w '%{http_code}' https://api.devlabperu.com/zync/api/clipboard/retention` → **401**
  (route registered + auth required). 404 means the migration/endpoint did not deploy — re-check Phase 7.2
  staged everything (see the v0.4.7 incident: an unstaged file shipped old behavior).
- `curl ... https://api.devlabperu.com/zync/health` → 200.
- In the deployed App: Clipboard → Settings shows "Keep clipboard for" with the preset selected; changing it
  persists (reload shows the new value).

---

## Pitfalls (learned the hard way — heed them)

1. **Staging:** after `git add`, ALWAYS `git diff --cached --name-only`. A typo'd path makes `git add` stage
   nothing; tests pass against the working tree, so the bug only shows in prod as old behavior. (This exact
   bug cost hours in v0.4.7.)
2. **DateTimeOffset + SQLite:** never compare `DateTimeOffset` columns in a LINQ `Where` translated to SQL —
   SQLite throws. Materialise the slim projection and compare in memory (Phase 3 does this).
3. **0 warnings:** the build must stay clean. If a new NuGet advisory (NU1903) appears, it is test-only SQLite
   and is already suppressed in the test csproj — do not touch production code for it.
4. **Migration must deploy:** the deploy's `[4/5] Applying migrations` step runs the efbundle; confirm the
   new migration file is committed (Phase 7.2) or the `Users.ClipboardRetentionHours` column won't exist and
   every clipboard append/purge/endpoint 500s.
5. **Do not break existing tests:** the EphemeralPurgeService constructor change adds OPTIONAL args, so
   existing `new EphemeralPurgeService(factory, logger)` test calls still compile. Keep them optional.
