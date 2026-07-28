# Plan — #398 slice: migrate `FilterSortGroupScreen` (F3) dispatch through the central table

> **Goal (from #398).** #355 introduced the central `(context, action) → key` table
> (`Tui/Screens/Keybindings.cs`) + `KeybindingDispatcher` and migrated only the **main-list**
> dispatch through it. The display side (`HelpItemSets`) is already asserted against the table for
> every context, so footers can't drift — but each non-main screen still dispatches with a literal
> `KeyCode` switch, so its *dispatch* isn't yet proven to agree with the table. #398 tracks migrating
> the remaining screens **one screen per PR**. This slice does **`FilterSortGroupScreen`** (F3).

## Why this screen (and not another)

- Two sibling slices are already in flight on #398: **PR #432** (`NotificationsFeedScreen`) and
  **PR #442** (`QuickOpenScreen`). `FilterSortGroupScreen` is a **third, non-overlapping** screen —
  no open PR touches `FilterSortGroupScreen.cs`, so this can't collide.
- Its command surface is a clean 1:1 with the table's `ScreenContext.FilterSortGroup` entries:
  `Help → F1`, `Back → Esc`. Both are already parameterless context-level handlers on the base
  `Screen` (`RequestHelp` / `Close`), so they drop straight into `KeybindingDispatcher.On(...)`.

## Current state (repo)

`FilterSortGroupScreen`'s screen-level `KeyDown` handler is a literal switch:

```csharp
KeyDown += (_, key) =>
{
    switch (key.KeyCode)
    {
        case KeyCode.Esc: key.Handled = true; Close(); break;
        case KeyCode.F1:  key.Handled = true; RequestHelp(); break;
    }
};
```

Form-focus keys stay where they are and are **out of scope** (correctly absent from the table):
the value `TextField`'s `Enter` (add-filter) and the filters `ListView`'s `Delete`/`Backspace`
(remove) are per-form keys, not cross-screen command shortcuts.

## Change

1. Build a `KeybindingDispatcher(ScreenContext.FilterSortGroup)` in the ctor, registering
   `KeyAction.Help → RequestHelp` and `KeyAction.Back → Close` (keys resolved from the table, never
   re-typed here). Replace the literal switch with `if (_keys.Dispatch(key)) key.Handled = true;`.
   This exactly preserves behaviour — Esc cancels (`Result` stays null), F1 opens Help — while making
   the table the single source of truth for both dispatch and the footer.
2. Add `EveryFilterSortGroupAction_DispatchesToItsOwnHandler` to `KeybindingDispatcherTests`,
   mirroring `EveryMainListAction_DispatchesToItsOwnHandler`: register every
   `Keybindings.ActionsFor(FilterSortGroup)` action and assert each one's table key dispatches to its
   own handler and no other. Together with the existing footer-agreement guard in `KeybindingsTests`,
   this proves **dispatch == table == footer** for the F3 context too.

## Invariants preserved

- **No second focusable pane (#3/#38).** Pure dispatch refactor; no view/layout change.
- **Bare letters reserved for type-ahead (#12).** The table holds only chords / function keys, so
  the dispatcher never intercepts a bare letter.
- No `Generated/` edit, no curated-spec change, no auth change.

## Verification

- `dotnet build -c Release` (0/0) + `dotnet test -c Release` green (new dispatch test + existing
  footer-agreement test cover the mapping).
- Manual (TUI, not CI): from the list press `F3`; `Esc` closes with settings unchanged, `F1` opens
  Help over the screen. Behaviour is identical to before the refactor.

## Deferred (tracked by #398, not this PR)

The remaining screens — `TaskDetailScreen`, `SettingsScreen`, `QuickUpdatesScreen`, `NewTaskScreen`,
`PromptTemplateEditorScreen`, `AgentRunScreen`, and the description-editor / comment-composer overlays
— stay literal for now; #398 remains open to track them, one screen per PR.
