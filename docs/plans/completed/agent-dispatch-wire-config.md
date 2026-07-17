# Plan — #91 (A1): Wire the inert AgentDispatch config into the live dispatcher

Part of epic #90. Foundation that unblocks the working-directory / prompt-threading
sub-issues (#92, #95, #97, #98, #100, #101, …).

## Problem

`#27` (S4) shipped `AgentDispatchSettings` (preferred terminal, `claude` exe + extra
args, working-dir mode + fixed dir, prompt preamble), a persisted `AppConfig.AgentDispatch`
block, and the F2 `SettingsScreen` UI to edit it — but **none of it is applied at dispatch
time**:

- `Tui/TodoApp.cs:49` — `_agent` is built once with defaults; the launcher gets no options.
- `Tui/TodoApp.cs:692` — `DispatchAsync(detail, comments, prompt)` — no working dir, no preamble.
- `Tui/TodoApp.cs:396` — the settings-close handler saves `_config.AgentDispatch` but never
  rebuilds `_agent`.
- `AgentDispatchSettings.ToLauncherOptions()` and `ResolveWorkingDirectory(...)` have **zero
  call sites** (grep-confirmed).

Both `ToLauncherOptions` and `ResolveWorkingDirectory` are already pure and unit-tested
(`AgentDispatchSettingsTests`); the gap is purely the call sites.

## Design

1. **`AgentDispatcher.DispatchAsync`** — add an optional `string? preamble = null` parameter
   (placed after `workingDir`, before `ct`) and forward it to
   `AgentPromptComposer.WritePromptFile(...)`, which already accepts a `preamble`
   (blank ⇒ default `Preamble`). This is the only production-code change to the Agent layer;
   `workingDir` already threads through to the launcher.

2. **`TodoApp`** — the wiring:
   - Change `_agent` from a field initializer to a field built in the constructor from
     `_config.AgentDispatch.ToLauncherOptions()` (a field initializer can't read `_config`).
     Add a private `BuildAgentDispatcher()` helper.
   - After the F2 settings dialog saves (`OpenSettings` close handler), rebuild `_agent` so a
     changed terminal / `claude` path / extra args apply **without a restart**.
   - In `DispatchAgent`, on the UI thread before the `Task.Run` hand-off, resolve the dispatch
     inputs from `_config.AgentDispatch`:
       - `workingDir = settings.ResolveWorkingDirectory(taskDerivedDirectory: null, home)`
         — the task-derived candidate stays `null` until #98 computes one; `Home`/`Fixed`
         modes resolve now. `home` = `Environment.GetFolderPath(SpecialFolder.UserProfile)`.
       - `preamble = settings.PromptPreamble`.
     Capture `_agent` into a local so a concurrent settings-save can't swap it mid-flight,
     then `await agent.DispatchAsync(detail, comments, prompt, workingDir, preamble)`.

3. **Zero-config invariant** — default settings project onto default `TerminalLauncherOptions`,
   `TaskDerived + null candidate` resolves to `null` (inherit), and a blank preamble keeps the
   default `Preamble`, so a user who has customised nothing sees byte-for-byte identical
   behaviour (same command, same cwd, same prompt).

## Tests (test-first)

`AgentDispatcherTests` (fake `ITerminalLauncher`, no process spawned):

- `DispatchAsync_PassesPreambleThrough` — a non-blank preamble reaches the composed prompt
  file (equals `Compose(task, comments, prompt, "custom")`, differs from the default).
- `DispatchAsync_BlankPreamble_UsesDefault` — null/blank preamble ⇒ default `Compose`.
- `Dispatcher_BuiltFromSettings_ForwardsOptionsWorkingDirAndPreamble` — mirrors the TodoApp
  glue: options from `ToLauncherOptions()`, working dir from `ResolveWorkingDirectory`, preamble
  from settings all reach the launcher / composed file.
- `Dispatcher_BuiltFromDefaultSettings_MatchesZeroConfigBehaviour` — default settings ⇒
  `claude`, no extra args, `Auto`, `null` working dir, default-preamble prompt.

`ToLauncherOptions` / `ResolveWorkingDirectory` themselves stay covered by the existing
`AgentDispatchSettingsTests`. TodoApp glue (Terminal.Gui) is verified by build + reasoning per
the repo's TUI rule — no new focusable pane, no keybinding change.

## Out of scope (deferred, tracked elsewhere)

- Real task-derived working-dir computation → **#98 (T1)**.
- Base working-dir capture in first-run/settings → **#92 (A2)**.
- Editable prompt template + PromptPreamble migration → **#100 (A3)**.
