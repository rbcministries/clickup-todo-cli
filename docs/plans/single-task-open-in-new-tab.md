# Ctrl+Enter → new terminal tab in single-task launch mode (#435)

Part of the multi-tab epic (#292). Follow-up to #384 (PR #434), which wired
**Ctrl+Enter → "open the current task in a new terminal tab"** into
`TaskDetailScreen` but scoped it to the **dashboard-hosted** detail. This wires
the same gesture for **single-task launch mode** (`SingleTaskApp`, `--task <id>`).

## Why this is now a bug fix, not just polish

When #435 was filed, single-task mode used the leaner `HelpItemSets.Detail`
footer set and had no Task Tree tab, so the gesture was neither advertised nor
fired there — the deferral was "a self-clone is low-value, leave it out."

Since then **#374** landed the Task Tree tab **in single-task mode**:
`SingleTaskApp.BuildDetailTab` constructs `TaskDetailScreen` with a
`loadTaskTreeAsync` loader, so `_treeList` is **non-null** there. That flips two
things on `main` today:

- **The footer already advertises it.** `TaskDetailScreen.HelpItems` calls
  `HelpItemSets.DetailFooter(…, hasTaskTree: _treeList is not null)`, which now
  returns `DetailWithTaskTree` in single-task mode — and that set carries
  `Ctrl+↩ new tab`.
- **The gesture already fires.** The `OnKey` handler raises
  `OpenInNewTabRequested` gated on `_treeList is not null`, which is true.

But `SingleTaskApp` never subscribes to `OpenInNewTabRequested`. So single-task
mode **advertises a gesture that does nothing** — a dead key with a live footer
hint. The decision this issue asks for ("wire it, or won't-do") therefore
resolves firmly to **wire it**: the alternative (hide the hint) would regress the
tree-tab footer set that single-task mode legitimately shares.

## Acceptance criteria (from #435)

- From a `--task` single-task tab, `Ctrl+Enter` (or the footer hint) opens a new
  terminal tab (or the documented copy-command fallback).
- The footer advertises it under the same `Ctrl+Enter` key as the dashboard
  (already true via `DetailWithTaskTree`).

## Design

The launch primitive already exists and is host-agnostic:
`AppLaunchCommand.ForTask(id)` (pure, #301) resolves how to relaunch this app,
and `ITerminalLauncher.LaunchAppAsync(command, options)` runs it through the same
cross-platform emulator matrix agent-dispatch uses, preferring a new tab. The
dashboard's `TodoApp.LaunchAppTabForTask` orchestrates it: build options → launch
off the UI thread → flash success naming the terminal, or the copy-command
fallback when no emulator can be launched.

### Shared, pure pieces — `src/ClickUpTodo/Agent/AppTabLaunch.cs` (new)

To keep the two hosts from drifting, the parts most prone to silent divergence
move into a pure, unit-tested helper (no Terminal.Gui dependency — primitives in,
strings out):

- `Options(PreferredTerminal, string? customTerminalCommand)` → the
  `TerminalLauncherOptions` for an app-tab launch: `LaunchLocation.NewTab`,
  the Windows preferred terminal, and the parsed custom terminal command.
  Deliberately **not** `AgentDispatchSettings.ToLauncherOptions()` —
  `ClaudeExecutable`/`ExtraArgs` are a dispatch concern and don't apply to
  relaunching this app (the dashboard's existing comment says exactly this).
- `Opening(name)` / `Opened(name, LaunchResult)` / `Fallback(command, copied,
  reason?)` — the three status strings, composed identically for both hosts.

### Dashboard — `TodoApp` (refactor, behaviour-preserving)

`LaunchAppTabForTask` and `FlashLaunchFallback` are re-expressed in terms of the
new helper. No behaviour change; the strings and options are byte-identical to
today's inline versions (locked in by the unit tests).

### Single-task — `SingleTaskApp` (the wire-in)

- Add `ITerminalLauncher _tabLauncher = new TerminalLauncher()` and a
  `bool _launchingTab` re-entrancy guard (mirroring the dashboard fields).
- Add `LaunchAppTabForTask(taskId, name)` — the same off-UI-thread orchestration
  as the dashboard, using the shared helper and this host's `Flash`, with a
  `FlashLaunchFallback` that copies the command to the clipboard
  (`Clipboard.TrySetClipboardData`, already reachable via `Terminal.Gui.App`).
- In `BuildDetailTab`, subscribe the screen:
  `screen.OpenInNewTabRequested += (_, _) => LaunchAppTabForTask(tab.TaskId, tab.Task.Name);`
  Keyed per `DetailTab`, so a task opened by walking the Task Tree tab (#374)
  launches **its own** task, and `tab.Task.Name` is read live so a mid-view
  refresh that renamed the task is reflected — exactly as the dashboard reads
  `screen.Task.Name`.

The single sectioned `ListView` input model (#3/#38) is untouched: no new
focusable pane, no new keybinding (the chord already exists), no list-source or
driver change.

### Stale-comment fix — `HelpLine.cs`

Two doc comments assert "single-task launch mode has no tree tab and keeps the
leaner `Detail` set" (on `DetailWithTaskTree` and `DetailFooter`). #374 made that
false. Corrected in passing to say single-task mode now also carries the tree tab
and so shares `DetailWithTaskTree`.

## Tests

- **`AppTabLaunchTests` (new, pure, CI):** `Options` sets `NewTab` + preferred +
  parsed custom command and leaves `ClaudeExecutable`/`ExtraArgs` at defaults;
  empty/whitespace custom command → empty argv; `Opening`/`Opened` (with and
  without a `Note`) and `Fallback` (copied vs not, with and without a `reason`)
  string composition.
- **No new E2E launch assertion.** Per #384 and the sibling launcher work, the
  actual launch leg starts a **real** emulator and so cannot be observed under
  the PTY harness; pressing `Ctrl+Enter` there would either find no emulator
  (fallback) or spawn a real process, neither a stable assertion. The existing
  `single_task_tree_check.py` / `single_task_launch_check.py` are re-run to
  confirm the footer/rendering is unregressed. The launch itself is verified
  manually (below) and described in the PR, as the dashboard gesture was.

## Manual verification (host code, not CI-unit-testable per `CLAUDE.md`)

Install the global tool, `clickup-todo --task <id>`, press `Ctrl+Enter`: a new
terminal tab opens running `clickup-todo --task <id>` (new window where the
emulator has no tab support); where no emulator can be launched the exact command
is copied to the clipboard and shown on the status line.

## Out of scope

- Giving single-task mode a *distinct* new-tab semantic (it re-launches the same
  task — "a bit odd but harmless" per #384; the copy-command fallback keeps it
  useful where a tab can't be targeted).
- Any change to `PlanAppLaunch`/the dashboard gesture's behaviour.
