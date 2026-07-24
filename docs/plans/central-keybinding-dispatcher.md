# Central, screen-context-aware keybinding dispatcher (#355)

Follow-up to #290 (PR #354), where a full dispatcher was explicitly deferred as
"a large refactor." Part of the Mouse interaction & UX polish epic (#283). The
maintainer floated the idea on #290:

> A central keybinding dispatcher that takes into account the current screen
> context … could be nice for human-maintainability.

## Problem

There is **no central keybinding dispatcher**: every screen hard-codes its own
`KeyDown`/`OnKey` switch (`TodoApp.OnListKey`, `TaskDetailScreen.OnKey`,
`NotificationsFeedScreen.OnKey`, `QuickUpdatesScreen`, Settings/FilterSortGroup/
NewTask, …). Only the **display** side is centralized, in `HelpItemSets`
(`Tui/Screens/HelpLine.cs`). So a shortcut and its footer label live in two
places and can drift. #290 fixed the concrete drift (Quick Updates → `Ctrl+U`
everywhere) by hand and added two guard tests
(`QuickUpdate_UsesCtrlU_OnBothListAndDetail`, `Refresh_UsesF5_OnEveryRefreshableScreen`),
but those only pin two actions on the **display** side — nothing links dispatch
to display.

## Verified current state (grounded in code)

- **Dispatch** is per-screen and literal: `TodoApp.OnListKey`
  (`Tui/TodoApp.cs:488`) branches on `key.IsCtrl` + `(key.KeyCode & ~CtrlMask)`
  for chords and `key.KeyCode` for function keys; `TaskDetailScreen.OnKey`
  (`:677`) and `NotificationsFeedScreen.OnKey` (`:167`) follow the same shape.
- **Display** is centralized: `HelpItemSets` holds one ordered `HelpItem` set per
  context; `HelpItem.ActionKey` (`Tui/Screens/HelpLine.cs:27`) is the parseable
  key token (used today for footer-click re-raise, `TodoApp.cs:1381`).
- The keys are canonical and round-trip: footer-click already does
  `Key.TryParse(item.ActionKey, …)` → `Application.RaiseKeyDownEvent(key)` and
  reaches the same handler as a physical press, so a token like `"Ctrl+U"`
  parses to the same `KeyCode` a physical `Ctrl+U` produces.

## Design — a single `(context, action) → key` table

Introduce one table as the **single source of truth**; both dispatch and the
help footer consult it (`HelpItemSets` **asserts against it**, per the issue's
"derived from it (or asserts against it)").

- **`Tui/Screens/Keybindings.cs`** — pure, no Terminal.Gui (mirrors `HelpLine`
  so it stays unit-testable):
  - `enum ScreenContext` — `MainList, Detail, DetailDescriptionEditor, Settings,
    FilterSortGroup, QuickUpdates, QuickOpen, NewTask, PromptTemplateEditor,
    NotificationsFeed, AgentRun, Help`. Extensible later for launch mode (#296),
    which is explicitly out of scope here.
  - `enum KeyAction` — the **command/navigation** actions the table governs:
    recurring ones (`Help, Back, Refresh, QuickUpdate, Feed, OpenInBrowser,
    ToggleCompleted, Open`) plus the per-context commands of the fully-covered
    screens (MainList: `OpenDetail, NewTask, QuickOpen, TogglePin, Settings,
    FilterSortGroup, CycleSubtasks, CycleBadges, Quit`; Detail: `DispatchToClaude,
    AddComment, EditDescription`; QuickUpdates: `Apply`; Feed: `MentionsOnly,
    ActivitySource`).
  - `static Keybindings` — `IReadOnlyDictionary<(ScreenContext, KeyAction),
    string>` mapping to the parseable token (identical to the matching
    `HelpItem.ActionKey`), with `Token`, `TryToken`, `All`, `ActionsFor` helpers.
- **`Tui/KeybindingDispatcher.cs`** — Terminal.Gui-aware. Given a
  `ScreenContext`, `On(KeyAction, Action)` resolves the token from the table
  (`Key.TryParse` → `KeyCode`) and registers a handler; `Dispatch(Key)` fires the
  handler whose `KeyCode` matches and reports whether it handled the key. So
  dispatch is **driven by the table**, not by literals.

### What the table deliberately does NOT govern (this slice)

Per-form focus keys that are screen-local rather than cross-screen actions —
in-form `Tab`/`Space`, NewTask/editor `Save` (`Enter` in a single-line form vs
`Ctrl+Enter` in the multi-line editor, legitimately different), and
`Ctrl+Alt+R` reset — stay in their screens' handlers. Undisplayed **aliases**
(`Ctrl+R` for `F5`, `Ctrl+C`/`Esc` quit on the main list) also stay literal, as
they are intentionally absent from the footer.

## Scope (this PR — the first slice)

The issue says this is "best done in slices (one screen at a time) so no single
PR is large." This slice delivers the table + dispatcher + the generalizing
tests, and **migrates the main-list dispatch** through it:

1. Add `Keybindings` (table) and `KeybindingDispatcher`.
2. Migrate `TodoApp.OnListKey`'s **command shortcuts** (the 15 footer-shown
   MainList commands) to a table-built `KeybindingDispatcher`. Movement/arrow/
   `Tab` handling and the undisplayed aliases stay exactly as they are — no
   behaviour change, same `key.Handled`/early-return control flow.
3. `HelpItemSets` is left byte-for-byte unchanged; a test asserts it against the
   table.

## Tests (pure, no TUI)

- **`AllBindingsOfAnActionShareOneKey`** — every `KeyAction` resolves to the same
  token in every context that binds it. Generalizes #290's `QuickUpdate → Ctrl+U`
  and `Refresh → F5` to all recurring actions (`Help → F1`, `Back → Esc`,
  `Feed → Ctrl+E`, `Ctrl+B`, …).
- **`FooterShowsTheTableKeyForEveryBinding`** — for every `(context, action)` in
  the table, that context's `HelpItemSet` contains an action item whose
  `ActionKey` equals the token (footer ⊇ table; the display never drifts from the
  source of truth).
- **`MainListDispatcher…`** — building the MainList dispatcher and dispatching
  each action's table key (`Key.TryParse`, the same path a footer click uses)
  fires the matching handler, and unrelated keys are not handled. This closes the
  loop: dispatch == table == footer for the migrated context.
- Existing `HelpLineTests` (incl. the #290 guards and the byte-for-byte
  `Format_MainList_RendersTheFullFooter`) stay green unchanged.

## TUI verification (not unit-testable in CI)

`dotnet build` clean; then `tui-validate`: from the main list, confirm every
migrated shortcut still fires (`Ctrl+U` Quick Updates, `Enter` detail, `Ctrl+O`,
`Ctrl+N`, `Ctrl+B`, `Ctrl+P`, `Ctrl+E`, `F1`–`F6`, `F12`, `Ctrl+Q`) and that
bare-letter type-ahead and arrow/`Tab` movement are unchanged.

## Deferred (out of scope) — tracked in a follow-up issue

- Migrating the remaining screens' dispatch (`TaskDetailScreen`,
  `NotificationsFeedScreen`, `QuickUpdatesScreen`, Settings, FilterSortGroup,
  NewTask, editors) to the dispatcher, one screen per PR. Their footers are
  already locked to the table by `FooterShowsTheTableKeyForEveryBinding`.
- Bringing per-form focus keys and launch-mode context (#296) into the table.

A new follow-up issue will track these and be linked from the PR.
