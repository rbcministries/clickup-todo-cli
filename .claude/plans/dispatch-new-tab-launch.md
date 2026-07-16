# Dispatch: launch an interactive session in a new tab of the current terminal (#255)

Follow-up to the dispatch working-directory fix (#252). For **interactive** dispatches, offer
launching the `claude` session in a **new tab of the terminal the app is already running in**, as an
alternative to today's always-a-new-window behavior. Opt-in preference, graceful fallback to a new
window where tabs aren't supported.

One-off (`-p`) dispatches now run through the background runner with no terminal at all, so "new
tab" is meaningless there — this only affects the interactive terminal path
(`TerminalCommandPlanner` / `TerminalLauncher`).

## Design

The existing `TerminalCommandPlanner.Plan(...)` already receives `getEnv` and returns an ordered list
of `LaunchSpec` candidates the launcher tries until one starts. A "new tab in the running instance"
becomes a **new candidate variant tried first** when we detect we're inside a supported host, with
the existing new-window spec(s) kept as the fallback. Detection is env-keyed and per-emulator.

### Support tiers (from the issue)

| Terminal | Detect via | New-tab command | Fallback |
|---|---|---|---|
| Windows Terminal | `WT_SESSION` non-empty | `wt -w 0 new-tab pwsh -NoExit -Command <cmd>` (`-w 0` targets the current window) | new-window chain |
| gnome-terminal | `GNOME_TERMINAL_SCREEN` / `VTE_VERSION` non-empty | `gnome-terminal --tab -- bash -lc <inner>` | new-window chain |
| konsole | `KONSOLE_VERSION` non-empty | `konsole --new-tab -e bash -lc <inner>` | new-window chain |
| iTerm2 (macOS) | `TERM_PROGRAM=iTerm.app` | osascript `create tab with default profile` + `write text <inner>` | Terminal.app window |
| Terminal.app (macOS) | `TERM_PROGRAM=Apple_Terminal` | — (no scriptable tab) | window (unchanged) |
| Generic `$TERMINAL` / `x-terminal-emulator` | — | — (no portable tab flag) | window (unchanged) |

The tab spec reuses the exact `command`/`inner` the window path builds, so the baked-in working
directory (`Set-Location` / `cd … &&`, #252) and the file-indirected prompt (`Get-Content -Raw` /
`$(cat …)`) ride along unchanged. Only the **host wrapper** differs (tab vs window).

### Phases

1. **Core model + planner (pure, unit-tested).**
   - New `TerminalLaunchLocation { NewWindow, NewTab }` enum + `TerminalLauncherOptions.LaunchLocation`
     (default `NewWindow` = today's behavior).
   - Thread `getEnv` into `PlanWindows` / `PlanMacOS` (Linux already has it). When
     `LaunchLocation == NewTab` and the host is detected, **prepend** the per-emulator tab spec ahead
     of the existing window chain. `EnvSet(getEnv, name)` = non-blank helper.
   - Unit tests: tab command per host when the env indicates it; window fallback when it doesn't
     (env absent, wrong host, or `NewWindow`); prompt stays file-indirected; cwd rides along.

2. **Config + settings wiring.**
   - `AgentDispatchSettings.LaunchLocation` (default `NewWindow`); fold into `IsDefault` and
     `ToLauncherOptions()`. Serializes as a readable string (JsonStringEnumConverter); absent in an
     old config ⇒ `NewWindow`.
   - F2 SettingsScreen: a "Launch" cycle button (New window / New tab where supported), mirroring the
     existing terminal/working-dir cycle buttons; threaded through the Save `AgentDispatchSettings`.
   - Tests: `AgentDispatchSettingsTests` (IsDefault-false case + ToLauncherOptions copy);
     `ConfigStoreTests` round-trip + readable-string + old-config-defaults.

## Invariants preserved

- Default (`NewWindow`) is byte-identical to today — all existing planner tests use `Defaults`, so
  they're unchanged.
- No `Generated/` edits, no spec change, no new API surface. Personal-token auth untouched.
- Single sectioned `ListView`; the settings change is one more cycle button, no second focusable pane.

## Deferred

- Per-dispatch override in the Dispatch pane (the issue lists it as optional) — the settings-level
  default is the meaningful slice; a per-dispatch toggle can follow if wanted.
- Terminal.app synthesized-Cmd+T tab (fragile, needs Accessibility permission) — stays window-only,
  as the issue directs.
