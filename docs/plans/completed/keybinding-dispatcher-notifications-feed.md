# Keybinding dispatcher — migrate `NotificationsFeedScreen` (#398)

Follow-up to #355 (PR merged), which introduced the central
`(context, action) → key` table (`Tui/Screens/Keybindings.cs`) and
`KeybindingDispatcher`, and migrated the **main-list** dispatch
(`TodoApp.OnListKey`). #398 tracks the remaining slices — one screen per PR. This
plan covers the **`NotificationsFeedScreen`** slice.

## Why this screen first

Of the screens still dispatching via a literal `key.KeyCode` switch, the feed is
the cleanest, lowest-risk migration:

- Its 8 footer-shown command keys map **exactly** to the 8 `NotificationsFeed`
  entries already in the table — nothing to add to `Keybindings.cs`:

  | Action | Key | Handler today |
  |---|---|---|
  | `Open` | `Enter` | `OpenSelectedTask()` |
  | `MentionsOnly` | `F3` | toggles `_mentionsOnly`, re-renders, flashes |
  | `Refresh` | `F5` | `RequestRefresh()` |
  | `ActivitySource` | `F6` | raises `ToggleActivityRequested` |
  | `ToggleCompleted` | `F12` | raises `ToggleCompletedRequested` |
  | `Feed` | `Ctrl+E` | `Close()` (List ↔ Feed toggle) |
  | `Help` | `F1` | `RequestHelp()` |
  | `Back` | `Esc` | `Close()` |

- Only **one** key stays outside the table: `Ctrl+R`, the undisplayed alias for
  the `F5` refresh key — intentionally not a footer command (mirrors how the
  main-list migration kept `Ctrl+R`/`Ctrl+C` aliases literal).
- No overlay/guard complexity (unlike `TaskDetailScreen`'s Dispatch pane /
  comment composer / description editor guards and its tree-tab special cases),
  no second focusable pane. The footer is already locked to the table by
  `KeybindingsTests.Footer_ShowsTheTableKey_ForEveryBinding`.

## Design

Mirror `TodoApp.BuildListKeyDispatcher` / `OnListKey` exactly:

1. Add a `KeybindingDispatcher _feedKeys` field, built once in the constructor
   (after the views are wired) via a private `BuildKeyDispatcher()` that registers
   the 8 actions above against `ScreenContext.NotificationsFeed`.
2. Rewrite `OnKey`:
   - Keep the `Ctrl+R` alias as an explicit pre-check (undisplayed, not
     table-governed), exactly as the main list keeps its aliases.
   - Then `if (_feedKeys.Dispatch(key)) { key.Handled = true; return; }`.
   - Any key the dispatcher doesn't claim (bare letters for type-ahead, ↑/↓
     movement) falls through untouched — `key.Handled` stays `false` — so the
     `ListView`'s native navigation and the `StatusBadgeListSource` type-ahead are
     unchanged. The table holds only chords / function keys, so a bare letter
     never matches.
3. Extract the F3 body into a small `ToggleMentionsOnly()` method so the
   dispatcher binds to a named handler (parity with the other one-liners).

`Feed` (`Ctrl+E`) and `Back` (`Esc`) both call `Close()`; they are distinct keys,
so the dispatcher's per-`KeyCode` registration has no collision.

No behaviour change: every key that was handled before is handled after, under
the same token, with the same `key.Handled = true` / early-return control flow.

## Tests (pure, no TUI)

Add to `KeybindingDispatcherTests` a per-screen closure test mirroring
`EveryMainListAction_DispatchesToItsOwnHandler`:

- **`EveryNotificationsFeedAction_DispatchesToItsOwnHandler`** — build the
  `NotificationsFeed` dispatcher registering every `Keybindings.ActionsFor(
  NotificationsFeed)` action to a distinct sentinel, dispatch each action's table
  key (via `Key.TryParse`, the same path a footer click uses), and assert it
  routes to its own handler and no other. Together with the existing
  `Footer_ShowsTheTableKey_ForEveryBinding` guard this proves
  **dispatch == table == footer** for the feed context.

Existing suites (`KeybindingDispatcherTests`, `KeybindingsTests`,
`HelpLineTests`, the feed's own `NotificationsFeedScreen*` pure-surface tests)
stay green unchanged — the feed's rendering/selection logic is untouched.

## TUI verification (not unit-testable in CI)

`dotnet build` clean, then `tui-validate` (after `dotnet test` is green): open the
feed with `Ctrl+E`, confirm `Enter` opens the selected task, `F3` toggles
mentions-only, `F5` / `Ctrl+R` refresh, `F6` toggles activity, `F12` toggles
completed, `F1` help, `Ctrl+E` / `Esc` return to the list, and that bare-letter
type-ahead + ↑/↓ movement are unchanged.

## Out of scope (remain tracked by #398)

The other screens' dispatch migrations (`TaskDetailScreen`, `QuickUpdatesScreen`,
`SettingsScreen`, `FilterSortGroupScreen`, `NewTaskScreen`, `QuickOpenScreen`,
`PromptTemplateEditorScreen`, `AgentRunScreen`, and the Task Detail editor
overlays) — one screen per PR, per #355's guidance. Their footers are already
locked to the table.
