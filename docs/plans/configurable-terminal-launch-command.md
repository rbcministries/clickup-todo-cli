# Plan — #385: configurable terminal launch command + cross-platform emulator preference

Issue: [#385](https://github.com/rbcministries/clickup-todo-cli/issues/385) (follow-up to
#301, part of epic [#292](https://github.com/rbcministries/clickup-todo-cli/issues/292)).
Resolves #301's remaining open question: _"Which emulators are first-class vs fallback-only?
Is a config setting for a user-specified launch command warranted?"_

## The gap this closes

`TerminalCommandPlanner` (`Plan` for the agent-dispatch `claude` launch, `PlanAppLaunch` for the
`clickup-todo --task <id>` tab launch #301) auto-detects a fixed matrix of emulators — Windows
Terminal / pwsh / powershell / cmd on Windows; Terminal.app + iTerm on macOS; a probe list
(`x-terminal-emulator`, `gnome-terminal`, `konsole`, `xfce4-terminal`, `alacritty`, `kitty`,
`wezterm`, `foot`, `xterm`, `terminator`) plus `$TERMINAL` and tmux on Linux. That covers the
common cases with zero config, but there is **no way to**:

- launch with an emulator **not in the probe list** (e.g. `ghostty`, `rio`, `st`, `contour`) or a
  **wrapper script**, and
- **prefer a specific emulator on macOS/Linux** — the `PreferredTerminal` setting is Windows-only
  today (it only reorders the Windows chain in `PlanWindows`).

## Design: one mechanism covers both asks

Add a single **user-specified terminal launch command** (a template) that, when set and its
executable is present, is emitted as the **highest-priority launch candidate on every platform**,
with the existing auto-detection chain preserved untouched as the fallback. Because an explicit
user-configured emulator is tried first, this both:

- lets the user name any emulator/wrapper (covers "not in the probe list"), and
- lets the user **prefer** a specific emulator on macOS/Linux (covers the Windows-only gap) —
  by naming it in the template.

This is the same "explicit preference moves to the front of the chain" shape the Windows
`PreferredTerminal` already uses, generalised to all three platforms and to arbitrary commands.
Windows `PreferredTerminal` is unchanged and still reorders the native chain behind the custom
command.

### Template shape and `{}` placeholder

The template is a shell-style command line, e.g. `alacritty -e {}`, `kitty {}`,
`wezterm start -- {}`, or a wrapper `myterm --exec {}`. It is tokenised (quote-aware) into an argv:

- the **first token** is the emulator executable (probed on `PATH`, exactly like every other
  candidate — if absent, the custom candidate is skipped and the normal chain runs, so an unset or
  unavailable command is a strict no-op / zero regression);
- a token exactly equal to the placeholder `{}` marks where the **host invocation** of the inner
  command is spliced in; if there is **no** `{}` token, the host invocation is **appended** (the
  bare-positional convention kitty/foot use).

The host invocation is the OS-native way the existing candidates already run the inner command:

- **POSIX (Linux + macOS):** `bash -lc <inner>` — the exact suffix `WindowArgs`/the emulator specs
  already use. `<inner>` is the planner's per-path shell string (a `cd … && claude …$(cat …)`
  dispatch, or `'clickup-todo' '--task' '<id>'` app launch).
- **Windows:** `<host> -NoExit -Command <command>` where `<host>` is pwsh (else powershell); the
  custom candidate is skipped when neither is present (the payload is a PowerShell command string).

The user therefore controls window-vs-tab entirely through their own template (e.g.
`gnome-terminal --tab -- {}`); the custom candidate itself carries no new-tab detection — it is a
deliberate, explicit launch.

## Why this shape

- **Zero regression by construction.** With no custom command (the default) or an absent executable,
  `Plan`/`PlanAppLaunch` return exactly today's candidate list. Every existing planner test stays
  green unchanged.
- **Reuses the argv discipline.** No prompt/command content is ever concatenated into a shell string
  by this feature; the custom candidate is an argv with the host invocation spliced in as argv
  tokens, matching the planner's "arrays, never strings" rule.
- **Testable end-to-end without a process.** Tokenisation is a pure helper; candidate construction is
  the pure planner. Both are unit-tested across all three OSes and both launch paths.
- **Shared surface.** The setting lives on `AgentDispatchSettings` (the one "terminal" surface #301's
  task-tab launch and the dispatch launch both already read), satisfying the issue's "consider
  unifying" note without a larger refactor of the two planner entry points.

## Phases

1. **Parser + options + settings (pure, fully tested).**
   - New `TerminalCommandParser.Parse(string?)` → `IReadOnlyList<string>` (quote-aware tokeniser;
     `Placeholder = "{}"`). Blank ⇒ empty list.
   - `TerminalLauncherOptions.CustomTerminalCommand` (`IReadOnlyList<string>`, default empty) — the
     tokenised template.
   - `AgentDispatchSettings.CustomTerminalCommand` (`string`, default `""`), folded into `IsDefault`
     and projected by `ToLauncherOptions()` via the parser.
   - Tests: `TerminalCommandParserTests`; extend `AgentDispatchSettingsTests`.

2. **Planner + launcher.**
   - Private `CustomLaunchSpec(exists, template, hostArgs, cwd)` in `TerminalCommandPlanner`;
     prepend it (when non-null) in `PlanWindows`/`PlanMacOS`/`PlanLinux` for both `Plan` and
     `PlanAppLaunch`. macOS must still emit the custom candidate when `osascript` is absent.
   - Tests: extend the planner suites (custom-first ordering, `{}`-splice vs append, skip-when-absent,
     Windows no-host skip, macOS-without-osascript, app-launch path).

3. **F2 dialog field + docs.**
   - A "Custom terminal cmd (`{}` = command)" label+field in `SettingsScreen`'s Dispatch column
     (free rows Y13/Y14 — no layout shift), threaded through `SettingsResult`/save.
   - Wire the parsed command into `TodoApp`'s app-launch `TerminalLauncherOptions` (task-tab path) so
     both launch paths honour it.
   - README: document the first-class (auto-detected) emulator set vs the custom-command escape
     hatch. Terminal.Gui screen is not CI-testable — verify by build + reasoning, describe manual
     steps in the PR.

## Out of scope (deferred)

- Fully unifying the planner's two entry points (`Plan` / `PlanAppLaunch`) — #385's notes flag this
  as optional internal cleanup; the custom command works through both without it.
- A structured per-emulator preference enum for POSIX — the free-form command template subsumes it.
