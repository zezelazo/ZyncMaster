# UI overhaul v0.5.0 — execution report (DONE)

Branch: `ui-overhaul-0.5.0` (off `main`). PR: https://github.com/zezelazo/ZyncMaster/pull/1
Stopped after Phase 9.3 (PR created). **Phase 9.4 release/deploy was NOT run** — handed back for review/release.

AI anonymity honored: no AI/Claude/Anthropic references and no `Co-Authored-By` in any commit, comment, or the PR.

## Phases completed

| Phase | Commit | Status | Files |
|------|--------|--------|-------|
| 1 — Native acrylic clipboard popup | `d87cef7` | done (manual acrylic check pending) | `src/ZyncMaster.App/Windows/ClipboardViewerWindow.axaml`, `…/ClipboardViewerWindow.axaml.cs`, `…/Windows/ClipboardRow.cs` (new), `…/Platform/Clipboard/Win32.cs`, `…/App.axaml.cs` |
| 2 — Status popup form + single force-sync + status icon | `b56eb03` | done | `ui/js/views/calendar-day.js`, `ui/css/calendar-day.css` |
| 3 — Calendar detail → modals, visible events, account selector, flat buttons | `99d7ec0` | done | `ui/js/views/calendar-day.js`, `ui/css/calendar-day.css` |
| 4 — One-click connect for signed-in calendar | `077a469` | done (manual OAuth check pending) | `ui/js/views/calendar.js` |
| 5 — Home horizontal overflow | `6329591` | done | `ui/css/shell.css`, `ui/css/layout.css` |
| 6 — Devices identity + this-device card | `41aeb8d` | done | `ui/js/views/devices.js`, `ui/css/components.css` |
| 7 — Navigation consistency | — (no change) | verified OK | report `plans/reports/07-nav-OK.md` |
| 8 — Optional visual polish | — (no change) | skipped (needs approval) | report `plans/reports/08-polish-skipped.md` |
| 9 — Version + tests + PR | `4e51524` | done (9.4 not run) | `src/ZyncMaster.App/ZyncMaster.App.csproj`, `ui/js/app.js` |

## Gates

- Release build (`dotnet build -c Release --nologo`): **0 warnings, 0 errors**.
- Full suite (`dotnet test -c Release`): **2007 passed, 6 skipped (Postgres — no live DB), 0 failed**.
  - Core 88 · Graph 151 · CalExport 196 · Engine 255 · App 344 · Server 973 · Postgres 6 (skipped).
- Per-phase: C# phases gated on `dotnet build`; UI-only phases gated on `node --check` (JS) + CSS brace
  balance. The full test suite was deferred to Phase 9 per the user's instruction (tests cost too much
  time/tokens to run per phase). No xUnit test exercised the old WebView2 popup, so none were removed.

## Deviations from the draft plan (all to use REAL APIs / make the plan's design actually work)

**Phase 1**
- `PasteClipboardEntryAsync(id)` takes only the id (the plan guessed `(id, PriorForeground)`); it resolves
  the captured paste target via `PasteTargetWindowProvider` and dismisses the viewer via `CloseClipboardViewer`
  internally — both already wired in `App.axaml.cs`.
- History comes from `GetClipboardHistoryAsync()` → `IReadOnlyList<ClipboardHistoryItem>` (async; plan
  assumed a sync `GetClipboardHistory()` over `ClipboardEntry`). Rows map from `ClipboardHistoryItem`.
- The wire item has no file name, so a File row shows the label "File" (text rows show an 80-char preview,
  image rows "Image"). Meta = `{originDeviceName} · {short age}`.
- Removed now-unused `_clipboardViewerBridge` / `_clipboardViewerHost` fields and their refs (§1.7); the
  native viewer refreshes via `RefreshClipboardViewerRows` on each history mutation.

**Phase 2**
- Status health uses the REAL signals `(lastResult.failed||0) > 0 || pinnedDeviceOnline === false`
  (matching `status-model.calendarDot`); the plan's `lastResult.error` field does not exist.

**Phase 3** (largest deviation — modal integration)
- The real `openModal` supplies its own title bar + close (✕/Esc/backdrop) and returns `{ close }`. Keeping
  the panels' own `<header>` would double the title/✕, and wiring those ✕ to a state-only `close` would not
  dismiss the modal. So: the three panels' headers were removed, `close` = `modal.close()`, and Prefix-rules
  round-trips back to Replicate via the modal `onClose` (+ a selection capture/restore around the swap).
- Added CSS the plan referenced but didn't define: `.calday--full` (single-column grid after the aside was
  removed), `.calday-legend-chip` (legend items are now `<button>` toggles), and a `.calday-modal` padding
  tidy so the panels sit cleanly inside the modal card.
- Kept the `delete calDay.days[...]` cache invalidation in New-event create (the plan's "…" shorthand).
- Cosmetic edge (verbatim from the plan): the legend swatch color is indexed over ALL accounts while the
  grid columns are indexed over the VISIBLE accounts, so hiding an account can reassign column colors.

**Phase 4**
- Real connect action is `connectCalendar` (not `connectCalendarGraph`), payload `{ scope }`, 210 s timeout.
  Reused the existing `connectAccount(...)` helper rather than a raw bridge call.
- `loginHint` is unsupported by the bridge action → dropped (no server change; Phase 4 is UI-only, so the
  server build/test in §4.3 was not needed).
- Identity comes from `identityAuth.me` (the desktop identity), NOT `live.me` (only set in the web panel —
  it is null in the app, where this screen runs). Added `identityAuth` to `calendar.js`'s ctx destructure.

**Phase 6**
- Current-device name accessor: the roster's `isThis` entry → `live.device.name` → `settings.deviceName`
  (the plan's `live.thisDevice` does not exist). Identity from `identityAuth.me` (added `identityAuth` to
  `devices.js`'s ctx).

## Post-plan follow-ups (completed this session, all pushed to the PR)

- **Testable mapper:** extracted the row-mapping helpers to `ClipboardRowMapper` (internal static, time
  injected) + 44 unit tests (`ClipboardRowMapperTests`). App.Tests 344 → 388.
- **e2e specs:** updated `calendar-app-day.spec.js` for the modals + single force-sync (5/5 green in real
  Chromium); deleted the obsolete `clipboard-viewer.spec.js`; updated the e2e README.
- **Dead assets:** removed `ui/clipboard-viewer.html`, `ui/js/clipboard-viewer.js`, and the `.cb-viewer*` /
  `.cb-stage` overlay rules from `clipboard.css` (shared dashboard `.cb-*` classes kept); tidied stale
  comments.
- **CI was red, now green:** the `js` CI job runs `node --test tests/js-unit/*.mjs` (a job the autonomous
  run didn't know about). The status-popup redesign broke 11 js-unit tests — the new status-icon used
  `classList` the DOM shim lacked (crash) and the assertions referenced the old DOM. Fixed the shim
  (`classList` polyfill) and updated the assertions to the form + single force-sync. js-unit 73/73.
- **CI hygiene:** restricted `push` to `main` (PR branches were double-running push+PR); added a docs-only
  `paths-ignore`; removed the obsolete Azure `deploy-server.yml` (superseded by the VPS deploy).
- **Phase 8 (visual polish):** approved + done — see `08-polish.md`.
- **Cleanup:** gitignored the runtime `clipboard-blobs/`; synced the CLAUDE.md test count (local, gitignored).

## Still pending (yours — cannot be automated)

- **Manual (visual) — Phase 1.8:** confirm the native clipboard popup is acrylic-translucent with rounded
  corners on Windows 11 (22621+). Code is in place; this needs the running desktop app. Fallback if not
  acrylic (approval required): a `#CC0C0E16` Card background.
- **Manual (interactive) — Phase 4.3:** complete the one-click calendar OAuth round-trip.
- **Review + merge PR #1**, then **release** (`release-app.yml --ref main -f version=0.5.0`). No server
  deploy needed (no backend change in this PR).
- **Visual eyeball of Phase 8:** the flat-button look now applies app-wide; revert `acfb87d` if unwanted.
