# Plan: Contextual single-line help footer (#103, part of #102)

## Problem

Every screen hand-rolls its own footer `Label`, and the active screen's hint
**stacks on top of** the main list's help line instead of replacing it:

- The main-list `help` Label lives at the window's bottom row
  (`TodoApp.cs:196-202`), with `_statusLabel` just above it (`:195`); both are
  children of `_window` and always visible.
- `Screen` is sized `Dim.Fill(2)` (`Screen.cs:25`) and `ShowScreen` hides only
  the list *frame* (`TodoApp.cs:439`), so the list's `help` line stays visible
  under any open screen.
- The detail screen adds its own `hint` at `Pos.AnchorEnd(1)` **within the
  screen** (`TaskDetailScreen.cs:115-121`), rendering a second help line above
  the still-visible list one. Settings / Filter-Sort-Group / StatusPicker do
  the same.

Result: the long main-list shortcut line sits under every screen at all times,
and each screen duplicates footer wiring.

## Goal

One **contextual** help line, owned by the window, whose content is driven by
the active context: it shows the currently-focused screen's shortcuts and
nothing else; the list's shortcuts appear only when the list is active. This is
the foundation #H2 (#104, responsive truncation) builds on.

## Design

### Pure, unit-tested model (`Tui/Screens/HelpLine.cs`) — the testable core

Mirrors the repo's `SettingsForm` / `StatusPickerModel` pattern (screen logic in
a pure companion, xUnit-tested; Terminal.Gui glue stays thin).

- `readonly record struct HelpItem(string Key, string Label)` — one shortcut.
- `static class HelpItemSets` — the canonical ordered item list for each
  context: `MainList`, `Detail`, `Settings`, `FilterSortGroup`, `StatusPicker`,
  `Help`. This is the "which items for which context" data.
- `static class HelpLine`:
  - `Format(IReadOnlyList<HelpItem>)` -> `"key label · key label · …"`.
  - `ForActiveScreen(IReadOnlyList<HelpItem>? screenItems, IReadOnlyList<HelpItem> fallback)`
    -> the selection rule (screen items when a screen is active, else the list's).

`MainList` is defined so `Format(MainList)` reproduces today's main-list help
string **byte-for-byte**, so the default (list-active) footer is unchanged.

### `Screen` base (`Tui/Screens/Screen.cs`)

- `public abstract IReadOnlyList<HelpItem> HelpItems { get; }` — every screen
  declares its footer shortcuts (forces each to opt in; no silent empty footer).
- `event EventHandler<string>? FlashRequested` + `protected RequestFlash(msg)` —
  a transient message channel back to the host's status line. Replaces
  FilterSortGroup's practice of overwriting its hint Label with a validation
  error (issue: "preserve that behaviour, e.g. a transient message channel …").
- `event EventHandler? HelpRequested` + `protected RequestHelp()` — lets any
  screen open Help on **F1**.

### `TodoApp` — single shared footer + screen stack

- Promote the local `help` Label to a field `_helpLabel` (the persistent bottom
  row, still window-owned). `UpdateHelpLine()` sets its text from
  `HelpLine.ForActiveScreen(ActiveScreen?.HelpItems, HelpItemSets.MainList)`.
  Call it on every screen show/close.
- **Screen stack.** Replace the single `Screen? _activeScreen` field with a
  `List<Screen> _screens`; `ActiveScreen` = top of stack. `ShowScreen` pushes
  (hiding the current top, or the list frame when the stack is empty);
  `CloseScreen(screen)` pops and restores the previous top (or the list). This
  keeps the **single-toplevel, one-visible/focusable-screen, no-nested-run-loop**
  invariants (#3/#38) — lower screens are `Visible=false` — while letting F1
  open Help *over* a screen and Esc return to that screen with its state intact
  (no close-and-lose-edits). List-initiated opens still guard on
  `ActiveScreen is null`, so only Help ever stacks in practice.
- Wire each pushed screen's `FlashRequested` -> `Flash`, and `HelpRequested` ->
  push a `HelpScreen` (unless the top already is one).
- `_statusLabel` (transient status/flash) is untouched — it stays on its own row
  above the shared help line.

### Screens

Each screen: declare `HelpItems`, delete its hand-rolled hint `Label`, and add
an **F1 -> `RequestHelp()`** case to its key handler. `TaskDetailScreen` gains
F1 (it had none). `FilterSortGroupScreen` routes its filter-validation error to
`RequestFlash(error)` instead of `hint.Text`.

### Screen sizing

No change needed. With a single window-owned footer whose text is swapped per
context (rather than each screen drawing its own), `Dim.Fill(2)` already leaves
exactly the status + shared-help rows visible and uncovered — there is no longer
an "old list help line" underneath, because it *is* the one contextual line.

## Tests

- `HelpLineTests` (pure): `Format` joins correctly / handles empty; `MainList`
  formats byte-for-byte to today's string; each set is non-empty and every
  screen's set ends with an `Esc`/close item and (except Help) offers `F1`;
  `ForActiveScreen` picks screen items when present, else the fallback.
- Terminal.Gui surface (single line, no stacking, correct on show/close, F1 from
  a screen, Esc back to the underlying screen) verified by build + reasoning per
  the repo's TUI rule, then `tui-validate` (after `dotnet test` is green) to
  assert the rendered footer text and confirm no output/latency regression.

## Phases

1. Pure `HelpLine` / `HelpItem` / `HelpItemSets` + `HelpLineTests`.
2. `Screen` base additions; `TodoApp` shared footer + screen stack + wiring;
   each screen declares items, drops its hint, handles F1. Build + test + format.
3. `tui-validate`; PR finalize.

## Out of scope (deferred)

- **Responsive truncation / overflow to an "F1 Help + Shortcuts" affordance** —
  that is #H2 (#104), which this is the foundation for.
- Any change to `HelpScreen`'s master shortcut text content beyond wiring
  (its stale "excluded statuses" line is #69 follow-up, not this).
- #93's Dispatch-trigger footer entry — whichever of #93/#103 lands second adds
  the Dispatch item to the detail screen's declared set (coordination note).
