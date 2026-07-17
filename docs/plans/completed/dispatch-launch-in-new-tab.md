# Plan — Dispatch: launch an interactive session in a new tab (#255)

Follow-up from the dispatch working-directory fix (PR #252, merged). For **interactive**
dispatches, offer launching the `claude` session in a **new tab of the terminal the app is
already running in**, as an opt-in alternative to today's always-a-new-window behavior, with a
graceful fallback to a new window wherever tabs aren't supported.

## Acceptance criteria (from the issue)

- A "launch location" preference (new window = default/current behavior vs. new tab) in F2 settings.
- Env-keyed detection helpers.
- Per-emulator new-tab `LaunchSpec` variants in `TerminalCommandPlanner`, gated on detection, tried
  before the new-window fallback.
- Fallback to a new window whenever tabs are unsupported or detection fails.
- Unit tests mirroring `TerminalLauncherTests`: the tab command per host when the env indicates it,
  and the window fallback when it doesn't.
- The baked-in working-directory `cd`/`Set-Location` (from #252) rides along on the tab path.

## Support tiers (issue table)

| Terminal | Detect via | New-tab command |
|---|---|---|
| Windows Terminal | `WT_SESSION` | `wt -w 0 new-tab pwsh -NoExit -Command …` (`-w 0` = current window; today's `wt new-tab` opens a new window) |
| gnome-terminal | `GNOME_TERMINAL_SCREEN` / `VTE_VERSION` | `gnome-terminal --tab -- bash -lc …` (shared server → lands in the existing window) |
| konsole | `KONSOLE_VERSION` | `konsole --new-tab -e bash -lc …` |
| iTerm2 (macOS) | `TERM_PROGRAM=iTerm.app` | osascript `create tab with default profile` + `write text` |
| Terminal.app (macOS) | `TERM_PROGRAM=Apple_Terminal` | — window-only (do script only makes windows) |
| Generic `$TERMINAL` / `x-terminal-emulator` | — | — window-only (no portable tab flag) |

## Design

- New enum `LaunchLocation { NewWindow, NewTab }` (in `Agent/TerminalLauncherOptions.cs`, beside
  `PreferredTerminal`). `TerminalLauncherOptions.LaunchLocation` defaults to `NewWindow` → every
  existing test and today's behavior are unchanged.
- Thread `getEnv` into `PlanWindows`/`PlanMacOS` (only `PlanLinux` got it before) so all three can
  probe detection env vars.
- `NewTabRequested(options, oneOff)` = `LaunchLocation == NewTab && !oneOff`. One-off `-p` terminal
  launches stay window-only — the issue notes new-tab is meaningless for one-off (it runs through the
  background runner with no terminal, and the interim terminal one-off path shouldn't grow tabs).
- Per emulator: when new-tab is requested **and** we detect we're inside that host, emit the tab
  `LaunchSpec` (DisplayName suffixed "(new tab)"); otherwise emit today's window spec. The tab spec
  replaces the window spec for that emulator — a present emulator's `Process.Start` doesn't fail on a
  valid `--tab`/`-w 0`/`--new-tab` flag, so a same-emulator window retry would be dead code; the
  cross-emulator chain (and, on macOS, the retained Terminal.app window spec) is the real fallback.
- macOS: iTerm2 tab spec is tried first when detected, with the Terminal.app window spec kept after
  it as the fallback (macOS has no cross-emulator chain otherwise).

## Config wiring

- `AgentDispatchSettings.LaunchLocation` (default `NewWindow`); `ToLauncherOptions()` projects it;
  `IsDefault` includes `LaunchLocation == NewWindow`. Serialized as a string via the existing
  `JsonStringEnumConverter`, so old configs load with the default.
- F2 `SettingsScreen`: a cycle button (mirrors the terminal/session-mode buttons) at the bottom of
  the Dispatch column.

## Phases

1. Core planner + options + tests (`LaunchLocation`, detection, per-emulator tab specs, `getEnv`
   threading; new `TerminalLauncherTests` cases for each host tab command, window fallback, cwd
   rides along, one-off stays window-only). Config projection (`AgentDispatchSettings`) + tests.
2. F2 `SettingsScreen` cycle button (TUI, build-verified).

## Out of scope (deferred, noted in the PR)

- The **optional** per-dispatch override in the Dispatch pane (issue lists it as "optionally"): keep
  this slice to the persisted default. If wanted later, it mirrors the #94 session-mode per-dispatch
  toggle.
- Terminal.app tab via synthesized Cmd+T / System Events (fragile, needs Accessibility) — stays
  window-only per the issue.
