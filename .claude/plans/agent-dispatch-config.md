# Agent dispatch: configuration (#27, S4 of epic #23)

## Goal

Make the agent-dispatch feature (#23) configurable, via `AppConfig` + the F2 settings
dialog, while keeping it **zero-config** (every setting optional with a sensible default).

Issue #27 asks for four configurable groups:

1. **Preferred terminal** — auto / Windows Terminal / pwsh / PowerShell / cmd.
2. **`claude` executable path + extra args** — the binary to invoke and args inserted
   before the prompt.
3. **Working directory** for the new session — task-derived / home / fixed.
4. **Optional prompt-template / preamble override** — replace the composer's fixed
   preamble line.

## Why this can land now (independent of #26)

`TerminalLauncherOptions` already exists on `main` and its own doc comment says it is
"the seam [#27] will populate." Nothing on `main` invokes the launcher/composer yet
(#26 wires the `A` key and is in flight in a concurrent session). So #27 is pure config
plumbing + a pure config→options mapping + a composer preamble hook + the F2 UI. #26 will
consume `AgentDispatch` when it lands (or use the launcher defaults if it lands first);
the collision surface is only the eventual call site, which #26 owns.

## Design

### Phase 1 — config model + pure mappers (fully unit-tested)

- New `Configuration/AgentDispatchSettings.cs`:
  - `PreferredTerminal PreferredTerminal` (reuse `ClickUpTodo.Agent.PreferredTerminal`).
  - `string ClaudeExecutable = "claude"`.
  - `List<string> ExtraArgs = []`.
  - `AgentWorkingDirectory WorkingDirectory` (new enum: `TaskDerived` (default), `Home`,
    `Fixed`) + `string FixedWorkingDirectory = ""`.
  - `string PromptPreamble = ""` (blank ⇒ composer default).
  - `bool IsDefault` for a "nothing customised" check.
  - `TerminalLauncherOptions ToLauncherOptions()` — coalesces blank exe → `"claude"`,
    copies extra args + preferred terminal.
  - `string? ResolveWorkingDirectory(string? taskDerived, string home)` — pure; returns
    the dir to start in (null = inherit current), per the mode. `#26` supplies the
    task-derived candidate and the home path.
- `AppConfig.AgentDispatch` property (`= new()`), so old config files (missing the key)
  deserialize to defaults — backward compatible.
- Composer preamble hook: add an optional trailing `string? preamble = null` to
  `AgentPromptComposer.Compose` and `WritePromptFile`; a non-blank value replaces
  `Preamble`. Backward compatible (existing/`#26` calls unaffected).

Tests: `AgentDispatchSettingsTests` (defaults, `IsDefault`, `ToLauncherOptions`
coalescing, `ResolveWorkingDirectory` for all three modes incl. blank-fixed → inherit),
composer preamble override (custom vs blank→default), and `ConfigStore` round-trip of the
new block (enum-as-string).

### Phase 2 — F2 settings dialog (build-verified; logic in pure helpers)

- Extend `SettingsForm` (pure, unit-tested):
  - `ParseExtraArgs(string?)` → `List<string>` (whitespace-split, trimmed, empties
    dropped).
  - `FormatExtraArgs(IEnumerable<string>)` → `string` (space-joined) for pre-filling.
- Extend `SettingsScreen`: add a right-hand "Agent dispatch" column (mirrors the two-column
  `FilterSortGroupScreen`) with: claude executable (TextField), extra args (TextField),
  terminal preference (cycle Button, like the sort-direction button), working-dir mode
  (cycle Button) + fixed-dir TextField, and a prompt-preamble TextField. No second
  focusable *list* pane beyond the existing pattern; all new widgets sit in the one screen.
- `SettingsResult` gains `AgentDispatchSettings AgentDispatch`; `TodoApp.OpenSettings`
  passes `_config.AgentDispatch` in and persists `result.AgentDispatch`.

Tests: `SettingsForm` arg parse/format round-trips + edge cases.

## Verification

- `dotnet build -c Release` (0/0) + `dotnet test -c Release` (all green; integration
  skipped without token).
- TUI: build-verified; manual check notes in the PR (open F2, edit each field, Save,
  confirm `config.json` shows the `agentDispatch` block; reopen shows persisted values).

## Deferred

- A fully **custom** terminal command (arbitrary emulator + arg template) beyond the
  planner's known set — would require `TerminalCommandPlanner` changes; out of scope here.
- Consuming these settings at the `A`-dispatch call site — that's #26's wiring.
