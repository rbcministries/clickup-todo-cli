# Standardize cross-screen shortcuts — Quick Updates → Ctrl+U everywhere (#290)

Part of the Mouse interaction & UX polish epic (#283). Issue #290, sub-issue (E).

## Problem

Similar actions are bound to different keys on different screens. The motivating
case: **Quick Updates opens with `Space` from the main list but `Ctrl+U` from
Task Detail**. Collapse to a single shortcut — `Ctrl+U` everywhere.

The repo has **no central keybinding dispatcher**; each screen hard-codes its own
`KeyDown`/`OnKey` switch. Help-line display, however, *is* centralized in
`HelpItemSets` (`Tui/Screens/HelpLine.cs`), which is the source of truth for the
contextual footer and is pure/unit-tested.

The root README's keyboard-shortcuts table is stale (maintainer confirmed in the
issue comment): it still lists `Space` = set status, `Enter` = open browser,
`R` = refresh, `P`/`?`/`Q` as bare keys — none of which match the current chords.

## Verified current state (grounded in code)

- **Main list** (`Tui/TodoApp.cs:OnListKey`): `Space` (`:534`) → `OpenQuickUpdates()`.
  Refresh is `F5` (`:582`) with an undisplayed `Ctrl+R` alias (`:486`).
- **Task Detail** (`Tui/Screens/TaskDetailScreen.cs:OnKey`): `Ctrl+U` (`:732`) →
  `QuickUpdatesRequested`. Refresh is `F5` (`:771`) + `Ctrl+R` alias (`:742`).
- **Feed** (`Tui/Screens/NotificationsFeedScreen.cs:OnKey`): refresh `F5` (`:188`)
  + `Ctrl+R` alias (`:175`).
- **Help sets** (`HelpItemSets`): `MainList` shows `␣ status` (`:94`); `Detail`
  already shows `Ctrl+U quick update` (`:123`); all three refreshable contexts
  already show `F5 ↻`.

**Conclusion:** refresh (`F5` + `Ctrl+R`) is *already* consistent across every
refreshable screen — that scope item needs a **guard test**, not a code change.
The only behavioural drift to fix is the Quick Updates launch key on the main
list.

## Scope (this PR)

1. **Main list: launch Quick Updates with `Ctrl+U`**, retiring `Space` as the
   launcher.
   - Add `case KeyCode.U` to the `key.IsCtrl` switch in `OnListKey` →
     `OpenQuickUpdates()`.
   - Remove the bare `case KeyCode.Space` → `OpenQuickUpdates()` block. `Space`
     falls through unhandled to the `ListView` (consistent with the "bare keys
     belong to the ListView type-ahead" model, #12).
2. **Help + docs sync:**
   - `HelpItemSets.MainList`: replace `new("␣", "status")` with
     `new("Ctrl+U", "quick update")` (identical wording to `Detail`).
   - Update the `QuickUpdates` help-set XML doc comment (`Space, #156` →
     `Ctrl+U`) and the `MainList` "byte-for-byte pre-#103" comment (no longer
     literally byte-for-byte after the intentional key change).
   - Rewrite the root README **Keyboard shortcuts** table to match the real
     current main-list bindings (source of truth: `HelpItemSets.MainList`), with
     a note that `F1` opens the full in-app per-screen list.
3. **Refresh consistency:** no code change; add a unit test pinning that `F5 ↻`
   appears on every refreshable help set so future drift is caught.

## Tests

`HelpItemSets` is pure and unit-tested (`tests/.../HelpLineTests.cs`), so the
standardization invariant is encodable as unit tests (no TUI needed):

- Update `Format_MainList_RendersTheFullFooter` expected string
  (`␣ status` → `Ctrl+U quick update`).
- Update `Fit_Truncates_KeepingLeadingPrefixThenFallbackLast`: at width 70 the
  longer item 2 (`Ctrl+U quick update`, 19 cols vs `␣ status` 8 cols) means the
  leading prefix that fits shrinks from 4 items to **3** (recomputed:
  `8 + 3 + 15 + 3 + 19 + 3 + 19(fallback) = 70`). Change `.Take(4)` → `.Take(3)`.
  (`Fit_ShowsMoreItems_AsWidthGrows` and `Fit_NeverDuplicatesF1` re-verified to
  still hold with the new widths — no change.)
- **New:** `QuickUpdate_UsesCtrlU_OnBothListAndDetail` — the `quick update`
  action carries the same `Ctrl+U` key in both `MainList` and `Detail`.
- **New:** `Refresh_UsesF5_OnEveryRefreshableScreen` — `F5 ↻` on `MainList`,
  `Detail`, and `NotificationsFeed`.

## TUI verification (not unit-testable in CI)

Build succeeds; describe in PR + optionally `tui-validate`:
- `Ctrl+U` from the main list opens Quick Updates; `Space` no longer does (falls
  through to the ListView, no crash / no stray marking).
- `Ctrl+U` still opens Quick Updates from Task Detail (unchanged).
- No other screen lost a shortcut.

## Deferred (out of scope) — with tracking

- **Esc / Enter / Tab cross-screen standardization.** The issue explicitly says
  to flag these for a maintainer decision rather than force uniformity. The Esc
  drift (quit-vs-back) is already tracked by **#298** (browser-style nav history,
  `Esc = back`) and **#299** (exit-confirmation modal on root). Noted in the PR;
  no new issue needed.
- **Central keybinding dispatcher** (a screen-context-aware action→key table).
  The maintainer floated this as "could be nice"; the issue says only introduce a
  shared table "if it can be introduced without a large refactor" — a full
  dispatcher is a large refactor. Deferred to a **new follow-up issue**, linked
  from the PR.
