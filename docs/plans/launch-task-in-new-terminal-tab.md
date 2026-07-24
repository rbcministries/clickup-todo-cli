# Launch a task in a new terminal tab — Ctrl+Enter / Ctrl+Left-Click

Issue: [#301](https://github.com/rbcministries/clickup-todo-cli/issues/301) (Multi-tab
sub-issue 8, epic [#292](https://github.com/rbcministries/clickup-todo-cli/issues/292)).
Depends on sub-issue (4) `--task <id>` (#296, **merged**) and reuses the mouse
row-hit-tester (#286, **merged**, `Services/RowHitTester`).

## Goal

Give the main list a gesture that opens the selected task in **its own terminal
tab/window** running `clickup-todo --task <id>`, so the multi-tab workflow (#292) is one
keystroke/click away:

- **Ctrl+Enter** (keyboard) — spawn a tab for the cursor's task.
- **Ctrl+Left-Click** (mouse) — spawn a tab for the clicked row (via `RowHitTester`).

On a supported/detected terminal it opens a **new tab**; where the host can't be driven
it falls back to a **new window**, and where no emulator can be launched at all the user
gets a clear, actionable fallback: the exact command is flashed on the status line and
copied to the clipboard.

## Why this shape

The agent-dispatch work (#25/#255/#307) already built a **cross-platform terminal
launcher** — `Agent/TerminalLauncher` over the pure `Agent/TerminalCommandPlanner`, which
encodes the whole emulator matrix (Windows Terminal / pwsh / powershell / cmd; macOS
Terminal.app + iTerm2 tabs via `osascript`; Linux `$TERMINAL` + gnome-terminal/konsole
tabs, the common emulators, and a tmux `new-window` path), including the **new-tab**
detection gates (`WT_SESSION`, `TERM_PROGRAM=iTerm.app`, `GNOME_TERMINAL_SCREEN`/`VTE_VERSION`,
`KONSOLE_VERSION`, `TMUX`). That planner is claude-specific only in **which command** it
runs — it builds `& 'claude' … (Get-Content …)` / `'claude' … "$(cat …)"` and wraps it in
the emulator. #301 needs the identical emulator wrapping around a **different** inner
command (`clickup-todo --task <id>`).

So rather than duplicate the matrix, this **reuses** it: the private per-OS spec builders
are parameterised on the already-built inner command, and a new `PlanAppLaunch` entry
feeds them an app-launch command instead of the claude one. The claude `Plan(...)` path is
unchanged (byte-for-byte identical command → its full existing test suite still pins it).

## Scope (this PR)

1. **`Agent/AppLaunchCommand`** (pure, unit-tested): a `(FileName, Arguments)` record + a
   resolver `ForTask(taskId, exists, processPath)` that decides how to relaunch *this app*:
   - `clickup-todo` on PATH → `("clickup-todo", ["--task", id])` (the installed global-tool
     / apphost case).
   - else the current process is a real apphost (its file name isn't `dotnet`) →
     `(processPath, ["--task", id])`.
   - else (a `dotnet run` dev launch, where the muxer can't be relaunched with `--task`) →
     best-effort `("clickup-todo", ["--task", id])`; the launch will fail to find it and the
     caller shows the copy-command fallback. Documented, not silently broken.
   - `ToDisplayCommand()` renders the shell-ready string for the fallback message/clipboard.

2. **`TerminalCommandPlanner` additive refactor**: lift the claude command construction up
   into `Plan(...)` (the private `PlanWindows/PlanMacOS/PlanLinux` now take the built
   command string), then add
   `PlanAppLaunch(os, exists, getEnv, AppLaunchCommand, options)` that builds the app
   command (`PwshAppCommand`/`PosixAppCommand`, reusing the existing `PwshQuote`/`PosixQuote`;
   no prompt-file indirection, no one-off `-p`, no keep-alive — the app is a long-running
   TUI that owns the new terminal). `cwd` is null (a single-task tab needs no working dir).

3. **Launcher**: `TerminalLauncher.LaunchAppAsync(AppLaunchCommand, options, ct)` reusing the
   existing OS / PATH-probe / env / process-start seams and the same
   "try candidates until one starts" loop (extracted to a shared private). Added to
   `ITerminalLauncher` as a **default-throwing interface member** (mirrors the repo's
   `GetTaskItemAsync` pattern) so the existing test doubles that implement only `LaunchAsync`
   still compile.

4. **TUI wiring (`TodoApp`)**:
   - **Ctrl+Enter** in `OnListKey` (a new case in the existing `IsCtrl` switch) → launch the
     cursor's task.
   - **Ctrl+Left-Click** in `OnListMouse` (alongside the existing double-click branch) →
     resolve the row via `RowHitTester` and launch. *(If the active driver doesn't report the
     Ctrl modifier on a mouse click, ship Ctrl+Enter + fallback and defer the mouse variant to
     a tracked follow-up — see Deferred.)*
   - `LaunchTaskInNewTab(TaskItem)`: resolve the command, launch off the UI thread with
     `LaunchLocation.NewTab` (reusing the user's preferred-terminal setting via
     `_config.AgentDispatch.ToLauncherOptions() with { LaunchLocation = NewTab }`), then flash
     the outcome; on failure flash the exact command and copy it to the clipboard
     (`Clipboard.TrySetClipboardData`, best-effort).
   - A **footer item** on `HelpItemSets.MainList` advertising the gesture
     (`Ctrl+↩` → "new tab", `Chord: "Ctrl+Enter"` so the #289 click re-raises a parseable key).

5. **README/help**: document the gesture, the supported emulator set (it's the same matrix as
   agent dispatch), and the copy-command fallback.

## Tests

- **`AppLaunchCommandTests`** (pure): each resolver branch (on-PATH / apphost / dotnet-muxer),
  id passed through, display-command quoting.
- **`TerminalCommandPlannerAppLaunchTests`** (pure): per-OS candidate ordering; new-tab
  emitted only when the emulator is detected (env-gated) else a window; `$TERMINAL` / tmux
  paths; no prompt-file / `-p` / keep-alive leaks into the app command; the app executable and
  `--task <id>` are quoted and present. Mirrors the existing `TerminalCommandPlannerTests`.
- **`TerminalLauncherAppLaunchTests`**: injected `start`/`exists`/`getEnv`/`os` — first
  candidate that starts wins; no-emulator → `Fail`; all-fail → `Fail`; not-on-PATH note.
- **Regression**: the full existing `TerminalCommandPlannerTests` stay green unchanged (the
  claude `Plan` output is identical after the refactor) — the safety net for the refactor.
- **TUI (not CI-unit-testable)**: a `tui-validate` scenario asserts the **resolved command +
  fallback** (spawning a real terminal isn't PTY-testable, per the issue's AC): inject a
  recording launcher into the harness and assert Ctrl+Enter resolves `clickup-todo --task
  <id>` for the cursor's task, and the no-emulator path flashes/copies the command.

## Deferred (out of scope, tracked)

- **Ctrl+Enter in the detail view** (the issue's open question — should the detail view also
  spawn a tab for the current task): a small follow-up once the list gesture lands. File an
  issue.
- **User-configurable launch command / first-class-vs-fallback emulator preference** (the
  issue's other open question): the reused matrix + preferred-terminal setting cover the
  common cases; a bespoke "launch command" setting is a follow-up. File an issue.
- **Ctrl+Left-Click** *iff* the driver mouse-modifier reporting turns out unavailable — else
  it ships here.
