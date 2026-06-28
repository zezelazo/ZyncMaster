# Phase 7 — Navigation consistency: OK (no changes)

Verified the primary navigation in `ui/js/app.js` (side-nav builder ~1594-1614, driven by
`registry.navItems()`). No concrete breakage found, so no code change was made.

## Nav items (each resolves to a registered view)

| Nav label | View id (navigate target) | order | section | hidden |
|-----------|---------------------------|-------|---------|--------|
| Home      | `home`         | 1   | modules | — |
| Calendar  | `calendar-day` | 2   | modules | — |
| Clipboard | `clipboard`    | 3   | modules | webPanel |
| Devices   | `devices`      | 90  | system  | webPanel |
| Settings  | `config`       | 100 | system  | — |

Rendered order (separator inserted before the first `system` item):
**Home, Calendar, Clipboard │ Devices, Settings.**

## Checks
- Every nav item's `navigate(v.id)` targets an existing `registry.register(...)` view
  (`home`, `calendar-day`, `clipboard`, `devices`, `config` all exist). No dangling targets.
- No duplicate paths to the same view.
- Devices and Settings are already top-level (system section), as the plan notes.
- `About` (`registry.register('about')`) is intentionally NOT a primary nav item — it is a
  sub-route reached from Settings, so its absence from the side-nav is by design, not breakage.

## Outcome
No changes. Nothing committed for Phase 7 (per the plan: "Commit only if changed").
