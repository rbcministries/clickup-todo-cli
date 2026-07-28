# Plan — #398 slice: migrate `QuickOpenScreen` dispatch through the central table

Issue: [#398](https://github.com/rbcministries/clickup-todo-cli/issues/398) — "Keybinding
dispatcher (follow-ups): migrate remaining screens' dispatch through the central table
(#355)". Follow-up to #355, which landed the `(context, action) → key` table
(`Tui/Screens/Keybindings.cs`) + `KeybindingDispatcher`, migrated the **main-list** dispatch
(`TodoApp.OnListKey`), and made the display side (`HelpItemSets`) assert against the table for
**every** context. #398 tracks migrating each remaining screen's *dispatch* — "one screen per
PR, per #355's guidance."

## Why this screen for this slice

- **Clean 1:1 with the table.** `QuickOpenScreen`'s command surface is exactly the three
  `ScreenContext.QuickOpen` entries — `Open` → `Enter`, `Help` → `F1`, `Back` → `Esc` — and each
  is a single context-level handler (`Submit` / `RequestHelp` / `Close`) with no per-pane /
  sender-dependent branching. That makes the migration a behaviour-preserving refactor with a
  tidy `EveryQuickOpenAction_DispatchesToItsOwnHandler` proof.
- **No collision.** In-flight PR #432 already takes the `NotificationsFeedScreen` slice; and
  `TaskDetailScreen` is currently touched by many open PRs (#412/#421/#422/#429/#434/#436/#439/
  #440/#441), so migrating it now would conflict. `QuickOpenScreen` is untouched by any open PR.
- **Not `QuickUpdatesScreen`:** its `Enter`/apply is sender-dependent (commits the *focused*
  Status/Priority pane), so `Apply` is not a single context-level handler — a poor dispatcher
  fit. Deferred to its own slice.

## Scope (this PR)

- `Tui/Screens/QuickOpenScreen.cs`: build one `KeybindingDispatcher(ScreenContext.QuickOpen)` in
  the ctor — `.On(Open, Submit).On(Help, RequestHelp).On(Back, Close)` — and replace the
  hand-rolled `switch (key.KeyCode)` in `OnKey` with `if (_keys.Dispatch(key)) key.Handled =
  true;`. Same handler is still wired to both `_input.KeyDown` and the screen `KeyDown`, so Enter
  submits from the text field and Esc always cancels, exactly as before. Non-matching keys fall
  through untouched (unchanged).
- `tests/ClickUpTodo.Tests/KeybindingDispatcherTests.cs`: add
  `EveryQuickOpenAction_DispatchesToItsOwnHandler`, mirroring the main-list test — every
  `QuickOpen` table key dispatches to its own handler and to no other. Together with the existing
  footer-agreement guard in `KeybindingsTests`, this proves dispatch == table == footer for the
  QuickOpen context too.

## Invariants honored

- **No `Generated/` hand-edit, no curated-spec change, no regen** — pure TUI-layer refactor.
- **Single focusable model (#3/#38) unchanged** — no new pane; `QuickOpenScreen` stays a modal
  with its one `TextField`.
- **Bare letters reserved for type-ahead (#12)** — the table holds only chords / function keys /
  Enter-Esc, so nothing here shadows a letter.
- Behaviour-preserving: the exact same keys (`Enter`/`F1`/`Esc`) route to the exact same handlers.

## Verification

- `dotnet build -c Release` (0/0) and `dotnet test -c Release` green (integration self-skips
  without `CLICKUP_TOKEN`); `dotnet format`.
- `tui-validate`: run the existing `quick_open_check.py` / `quick_open_followups_check.py` PTY
  scenarios to confirm Enter-opens / Esc-cancels / F1-help did not regress on the migrated screen
  (a behaviour-preserving refactor, so they should pass unchanged).

## Out of scope (remaining #398 slices)

The other screens named in #398 (`TaskDetailScreen`, `SettingsScreen`, `FilterSortGroupScreen`,
`NewTaskScreen`, `PromptTemplateEditorScreen`, `AgentRunScreen`, `QuickUpdatesScreen`, and the
Task Detail overlays) stay on their hand-rolled handlers — each is its own future slice. Per-form
focus keys (in-form `Tab`/`Space`, form `Save`) remain deliberately absent from the table.
