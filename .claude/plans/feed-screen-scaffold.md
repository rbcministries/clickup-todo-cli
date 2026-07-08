# Plan: Feed screen scaffold + navigation (#110, part of #109)

## Problem / goal

The Mentions & Comments feed epic (#109) needs its screen built as a **walking
skeleton first**, with no live data, so navigation and rendering can be
validated before the backend (comment fetch #112, aggregation #113, mention
filter #113, real rows #114) is wired in.

Deliver a new full-window `NotificationsFeedScreen` that opens from the main
list, renders a static empty-state placeholder, and closes back to the list with
focus restored — reusing the existing screen-navigation seam (#38), **not** a
nested `Dialog`/`Application.Run` loop.

## Acceptance criteria (from the issue)

- Screen opens from the main list, renders the placeholder, closes back with
  focus restored.
- `dotnet test clickup-todo.slnx` green.
- `tui-validate` covers open → render → close (only after tests are green).

## Design

Mirror the existing minimal screen (`HelpScreen` / `StatusPickerScreen`) and the
seam documented in `screen-navigation-seam.md`.

### `NotificationsFeedScreen : Screen` (`Tui/Screens/NotificationsFeedScreen.cs`)

- Title: `"Feed — mentions & comments"`.
- Body: a read-only `Label` filling the screen area, showing the empty-state
  placeholder text (a pure, unit-testable constant
  `NotificationsFeedScreen.EmptyStatePlaceholder` so the copy is pinned by a
  test even though the Terminal.Gui view can't be instantiated in CI).
- Key handling (same shape as `StatusPickerScreen`): `F1` → `RequestHelp()`,
  `Esc` → `Close()`.
- `HelpItems` → `HelpItemSets.NotificationsFeed`.
- `OnShown()` → `SetFocus()`.
- **No comment fetching, no service calls, no real rows** (explicit non-goal).

### Contextual footer (`HelpLine` / `HelpItemSets`)

- New `HelpItemSets.NotificationsFeed` screen set. Minimal + honest about the
  scaffold's real capability: `F1 help · Esc back`. (Scroll / open-task items
  arrive with #114/#115 when there is data to scroll/open.) Satisfies the
  existing screen-set invariants: offers `F1 help`, ends with an `Esc` item.
- Add `NotificationsFeed` to the `HelpLineTests` `AllSets` + `ScreenSets` theory
  data so the invariant tests cover it.
- Add `F5 feed` to `HelpItemSets.MainList` (right after `F4 subtasks`) so the
  binding is discoverable on the always-visible footer, consistent with every
  other screen-opening key (F1/F2/F3) being listed there. Update the pinned
  `Format_MainList_RendersTheFullFooter` expectation.

### Keybinding + wiring (`TodoApp`)

- **`F5`** on the main list opens the feed screen. Rationale: F1–F4 are the
  "open a view/screen" family (F1 Help, F2 Settings, F3 Filter/sort/group, F4
  Subtasks); F5 continues that family positionally and sits adjacent in the
  footer — the most discoverable, lowest-surprise choice. Trivially changeable
  by the maintainer since this is a scaffold.
- `OpenNotificationsFeed()` guards on `ActiveScreen is null` (like
  `OpenSettings`/`OpenDetail`) and calls `ShowScreen(new NotificationsFeedScreen(),
  static () => { })` — no result to read. It goes through the same guarded
  `CloseScreen` teardown path as every other screen.
- Add the `F5` case to the non-Ctrl `switch` in `OnListKey`, next to F1–F4.

### HelpScreen (F1) full list

- Add a line to `HelpScreen`'s body text documenting `F5  Open the mentions &
  comments feed`.

## Tests

- `HelpLineTests`: extend `AllSets` + `ScreenSets` with `NotificationsFeed`
  (picks up the non-empty / F1 / ends-with-Esc invariants); update the pinned
  main-list footer string for the new `F5 feed` item; add a focused test that
  `NotificationsFeed` renders as `"F1 help · Esc back"`.
- New `NotificationsFeedScreenTests`: pins `EmptyStatePlaceholder` (non-empty,
  mentions the feature) and that `HelpItems` is `HelpItemSets.NotificationsFeed`.
  (The screen's Terminal.Gui view isn't instantiated — no `Application.Init` in
  the test suite — so only the pure surface is asserted, matching the repo's
  `StatusPickerModel` pattern.)

## TUI validation

After `dotnet test` is green, run `tui-validate`: open the app, press `F5`,
assert the feed screen title + placeholder render; press `Esc`, assert the
dashboard is restored with the cursor intact; confirm keypress latency / output
volume unchanged and no second focusable pane added to the dashboard (#3).

## Out of scope (later issues in #109)

- Comment fetch in the facade (#112), aggregation service (#112), mention
  detection/filter (#113), real feed rows + list source (#114), open-task from a
  feed entry (#115), background refresh (#116).
