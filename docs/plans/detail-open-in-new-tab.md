# Ctrl+Enter in Task Detail — open the current task in a new terminal tab

Issue: [#384](https://github.com/rbcministries/clickup-todo-cli/issues/384) (follow-up to
[#301](https://github.com/rbcministries/clickup-todo-cli/issues/301), part of the multi-tab
epic [#292](https://github.com/rbcministries/clickup-todo-cli/issues/292)). Resolves one of
#301's open questions: *"Should Ctrl+Enter in the detail view (not just the list) also spawn a
tab for the current task?"* Parent #301 is **merged**, so the launch machinery this builds on
already exists.

## Goal

From **Task Detail** (the dashboard's detail screen), **Ctrl+Enter** opens the viewed task in
its own terminal tab — running `clickup-todo --task <id>` — reusing the exact launcher and
copy-command fallback the main list's Ctrl+Enter gesture already uses (#301). Clicking the
`Ctrl+↩ new tab` hint on the contextual footer does the same (footer action hints re-raise
their chord, #289).

## Why this shape

- **The launch machinery is already app-host-agnostic and unit-tested.**
  `AppLaunchCommand.ForTask(id)` resolves how to relaunch the app (covered by
  `AppLaunchCommandTests`), and `TerminalCommandPlanner.PlanAppLaunch` + `TerminalLauncher`
  perform the cross-platform tab/window launch (covered by #301). The list gesture's
  `TodoApp.LaunchTaskInNewTab` wraps them with the re-entrancy guard, the status flash, and the
  no-terminal clipboard fallback. The detail gesture reuses all of it — no new launch logic.

- **The detail screen already emits host-owned command requests as events.** Ctrl+B / Ctrl+U /
  Ctrl+O / the tree tab's Enter all raise events (`OpenBrowserRequested`, `QuickUpdatesRequested`,
  `QuickOpenRequested`, `OpenTaskRequested`) that the host acts on. A new
  `OpenInNewTabRequested` event follows that established seam, so the screen stays free of the
  process/terminal launch (which lives in the host) and the single-`ListView` input model
  (#3/#38) is untouched — no second focusable pane.

- **Scoped to the dashboard detail via the tree-tab seam, so it can't lie in single-task mode.**
  `TaskDetailScreen.HelpItems` already renders `HelpItemSets.DetailWithTaskTree` when the tree
  tab is present (`_treeList is not null`, i.e. the dashboard-hosted detail) and the leaner
  `HelpItemSets.Detail` otherwise (single-task launch mode, `SingleTaskApp`). Gating both the
  footer item **and** the `OnKey` handler on that same `_treeList is not null` condition keeps
  "the footer advertises it" and "the key does something" provably in lock-step: the dashboard
  detail gets the gesture; the single-task tab neither shows nor fires it. This mirrors how
  `Ctrl+O 🗁 by ID` is a detail footer command carried by the screen's hand-rolled `OnKey` +
  host wiring rather than a central-table (`Keybindings`) entry — so no table change is needed.

- **The composer's `Ctrl+Enter = save` is never disturbed.** `OnKey` already returns early
  while the comment composer or description editor is open (they own the keyboard and process
  their own `Ctrl+Enter` in `OnCommentKey`/`OnDescriptionKey`), so the new-tab chord can only
  fire when no composer/editor is focused. The handler is additionally inert while the Dispatch
  pane (`_promptBox`) is open, matching every other command chord in `OnKey`.

## Scope (this PR)

### 1. `TaskDetailScreen` — the new command seam

- A `public event EventHandler? OpenInNewTabRequested;` (documented like its siblings).
- In `OnKey`, a `Ctrl+Enter` branch — placed among the other `Ctrl`-chord commands, guarded on
  `!_promptBox.Visible && _treeList is not null` — that marks the key handled and raises
  `OpenInNewTabRequested`. The composer/editor early-return at the top of `OnKey` already
  shields the composer's save chord; the `_treeList` guard scopes it to the dashboard detail.
  (`Ctrl+Enter` carries `KeyCode.Enter | CtrlMask`, so it never trips the bare-`Enter` tree-row
  navigation branch, which matches `KeyCode.Enter` exactly.)

### 2. `TodoApp` — wire the event + share the launch core

- Extract the body of `LaunchTaskInNewTab(TaskItem?)` (command resolution → re-entrancy guard →
  off-thread launch → success flash / clipboard fallback) into a private
  `LaunchAppTabForTask(string taskId, string name)`. The list wrapper keeps its
  `task is null || ActiveScreen is not null` guard, then delegates; the detail path calls the
  core directly (from the detail screen, `ActiveScreen` *is* the detail — the list guard must
  not apply, mirroring how `OpenQuickUpdatesForDetail` deliberately skips it).
- Subscribe: `screen.OpenInNewTabRequested += (_, _) => LaunchAppTabForTask(resolvedId, detail.Name);`
  in the detail-construction block, alongside the other `screen.*Requested` wirings. The launch
  needs only the (fixed) task id; the name is cosmetic (the status flash).

### 3. `HelpItemSets.DetailWithTaskTree` — the footer hint

- Add `new("Ctrl+↩", "new tab", Chord: "Ctrl+Enter")` (identical to the main list's item) after
  the `Ctrl+O 🗁 by ID` entry. Only the tree-tab set gets it; the base `Detail` set
  (single-task mode) is unchanged.

## Tests

- **Unit (`HelpLineTests`):** the `DetailWithTaskTree` set carries `Ctrl+↩ new tab` re-raising a
  parseable `Ctrl+Enter` chord (mirrors the existing
  `MainList_CarriesCtrlEnterNewTab_ReRaisingCtrlEnter`), and the base `Detail` set (single-task
  mode) does **not** — so the gesture and its footer stay scoped to the dashboard detail. The
  existing `EveryActionItem_ReRaisesAParseableKey` theory picks the new item up automatically.
- **Unit (`AppLaunchCommandTests`, already green):** the launch resolution
  (`AppLaunchCommand.ForTask`) is the tested seam per the acceptance criteria; the launcher
  itself is covered by #301. No new resolution logic is introduced, so these stand as the
  regression guard for what the detail gesture resolves.
- **Build + reasoning (TUI glue, not CI-unit-testable):** the `OnKey` branch and the host
  wiring are Terminal.Gui glue; verified by a clean `Release` build and the reasoning above
  (`Ctrl+Enter` is not PTY-drivable and a real terminal can't be spawned in CI — same rationale
  as #301). Manual check for the maintainer: open a task's detail, press `Ctrl+Enter` (or click
  the footer hint) → a new terminal tab opens `clickup-todo --task <id>`; on a host with no
  driveable emulator the command is flashed and copied to the clipboard; the composer's
  `Ctrl+Enter`-to-save still works while composing.

## Out of scope (deferred)

- **`SingleTaskApp`'s detail carrying the gesture.** A single-task tab spawning another tab of
  *itself* (same `--task <id>`) is the low-value, "a bit odd but harmless" case #384 flags. It
  is deliberately left to the single-task-mode work; the tree-tab seam keeps it cleanly absent
  (no footer hint, no inert key) until then. Tracked separately (see the PR).
- Any central `Keybindings`-table entry for the detail new-tab command — like `Ctrl+O` on the
  detail, it stays a screen-hand-rolled footer command (the table is not the dispatch path for
  `TaskDetailScreen`, whose `OnKey` is hand-rolled pending #398).
- A configurable Ctrl+Click destination for task links (#320) and a user-specified launch
  command (#385) — separate issues.
