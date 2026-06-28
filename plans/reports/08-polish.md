# Phase 8 — Optional visual polish: DONE (approved post-run)

Phase 8 was approval-gated and was initially skipped during the autonomous run. The human later
approved extending the flat/minimal azure look, so it was completed as a follow-up.

## Required check: no violet in `ui/css/tokens.css`
Confirmed — no violet/purple in the token palette. Accents are azure / cyan / aqua/teal plus the
semantic warm/green/amber/red; surfaces are dark blue-gray navies. No global token was changed.

## What was done
Extended the flat (non-pill) radius from the calendar header to the shared component vocabulary,
keeping pill for genuinely pill-shaped elements:

- `.btn` (all app buttons) → `--r-sm` (was `--r-pill`).
- `.segmented` → `--r-sm`; `.segmented__item` → `--r-xs` (inner radius < outer, clean nesting).
- **Kept pill** (correct shapes, untouched): `.chip`, status dots, `.toggle`, `.slider`, `.glass--nav`
  (the floating nav bar), progress bars, scrollbar thumbs, and status pills like `.identity-waiting`.

Global tokens (`--r-pill`, `--r-sm`, color tokens) were NOT modified — only which radius the button /
segmented components reference. The calendar header's own `--r-sm` overrides (Phase 3 §3.6) are now
redundant but harmless and were left in place.

## Needs your eyes (visual)
This is a visual change across every screen's buttons. It is one commit (`acfb87d`) and trivially
revertible if the flat look isn't wanted app-wide. The clipboard dashboard's `.cb-action` row buttons
were left as-is (a niche component); say the word and I'll fold them in too.
