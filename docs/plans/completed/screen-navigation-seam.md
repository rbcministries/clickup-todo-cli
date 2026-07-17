# Plan: Settings & status-picker → full-window screens (#38)

## Problem

`SettingsDialog.Show` (F2) and `StatusPicker.Show` (Space) each spin up a
`Dialog` toplevel on a **nested `Application.Run(dialog)` loop** layered over the
main `Window`. That nested toplevel competes with the background refresh
(`Application.Invoke` → redraw) and repaints feel sluggish — the same class of
problem as the second focusable pane in #3. The single-`ListView` dashboard is
snappy precisely because it avoids extra toplevels/run-loops.

## Goal

Establish **one reusable full-window screen-navigation seam** inside the existing
single toplevel (no nested run loop), and route both **Settings (F2)** and the
**status picker (Space)** — plus the **Help (F1)** overlay, for consistency —
through it. The seam is what #17 (task detail view) will build on.

Acceptance (from the issue):

- Settings + status-picker open as full-window screens that feel immediate
  (verify on a real terminal — not measurable headlessly, per #3).
- `Esc` returns to the list with the cursor preserved on the same task.
- Status changes and settings saves behave exactly as today (optimistic
  in-place update #11, prefetch cache #10, excluded-status filtering).
- A single reusable screen-navigation seam exists for #17.

## Design

### The seam (`TodoApp`)

Swap content **within the existing `_window`** rather than running a second
toplevel:

- `private Screen? _activeScreen;`
- `ShowScreen(Screen screen, Action onClosed)` — hide `_frame`
  (`_frame.Visible = false`), `_window.Add(screen)`, wire `screen.Closed` to run
  `onClosed()` then tear down, call `screen.OnShown()` to focus the primary
  control.
- `CloseScreen()` — `_window.Remove(screen)`, dispose it, `_frame.Visible = true`,
  `_list.SetFocus()`. The cursor is preserved because the `ListView` and its
  selection are never rebuilt on close.

Only one screen at a time (`ShowScreen` no-ops if one is already active; the
open handlers guard on `_activeScreen`).

### `Screen` base (`Tui/Screens/Screen.cs`)

`abstract class Screen : FrameView` sized to `Dim.Fill()` × `Dim.Fill(2)` (leaves
the status + hint lines visible at the bottom, same as `_frame`). Exposes
`event EventHandler? Closed`, a protected `Close()`, and a virtual `OnShown()`
for initial focus.

### Screens

- `StatusPickerScreen` — `ListView` of statuses + hint; Enter sets `Chosen` and
  closes, Esc closes with `Chosen == null`. Host reads `Chosen` in `onClosed`.
- `SettingsScreen` — same layout as today's dialog; Save sets `Result` and
  closes, Cancel/Esc close with `Result == null`.
- `HelpScreen` — read-only text; Esc/Enter close.

### Pure, unit-tested logic (mirrors the repo's formatter pattern)

- `StatusPickerModel.PreselectedIndex(statuses, current)` + `FormatStatus(...)`.
- `SettingsForm.ParseRefreshSeconds(text, fallback)` (clamp 10–3600) +
  `CanAdd(existing, candidate)` (non-blank, case-insensitive de-dupe).

### Refresh while a screen is open

`RefreshService` keeps running. `OnTasksLoaded`/`Render` only ever set
`_list.Source` / `_list.SelectedItem` and update the (still-visible) status
label — **never** `SetFocus` — so a refresh cannot steal focus from the active
screen. The cursor target (`keepTaskId`) is captured from the row the user was
on, so it's preserved across both the refresh and the screen round-trip.

## Phases

1. Pure helpers (`StatusPickerModel`, `SettingsForm`) + their unit tests.
2. `Screen` base + `StatusPickerScreen` / `SettingsScreen` / `HelpScreen`;
   wire the seam into `TodoApp`; delete `StatusPicker.cs` / `SettingsDialog.cs`;
   update help/hint text. Build + test + format.

## Out of scope

- The Task Detail screen itself (#17 / PR #33) — this only delivers the seam it
  will adopt.
- Any change to the status write path, prefetch cache, or exclusion filtering.
