# Cross-platform (2): broaden Linux terminal detection for agent dispatch

Issue: [#307](https://github.com/rbcministries/clickup-todo-cli/issues/307) (child of
the cross-platform release-readiness epic [#312](https://github.com/rbcministries/clickup-todo-cli/issues/312)).

## Context — what already exists

Agent dispatch opens an interactive `claude` session in a new terminal. The launch
logic is a pure, I/O-free planner (`TerminalCommandPlanner`) plus a thin orchestration
launcher (`TerminalLauncher`). When #255 added new-tab support it also grew macOS and
Linux planning, so on `main` today:

- **macOS** — `osascript` drives Terminal.app (window) with an iTerm2 new-tab path
  gated on `TERM_PROGRAM=iTerm.app`. ✅ Meets the issue's macOS bullet.
- **Graceful degradation** — `Plan` returns `[]` when nothing is found and the launcher
  reports `"No terminal emulator found to launch Claude."` (no crash / silent no-op). ✅
- **One-off `claude -p`** — POSIX/macOS keep-alive so the window doesn't vanish. ✅
- **New-tab vs new-window** — detection-gated per host, falls back to a window. ✅
- **Linux** — honors `$TERMINAL`, else probes **only** `x-terminal-emulator`,
  `gnome-terminal`, `konsole`. gnome-terminal/konsole get detection-gated tab specs.

## The gap this plan closes

The issue's Linux bullet asks to detect `x-terminal-emulator`, `gnome-terminal`,
`konsole`, **`xterm`, …** *and/or a `tmux`/multiplexer path*. The current probe list
stops at three, so:

1. **`xterm`** (named in the issue) and every modern emulator (alacritty, kitty,
   wezterm, foot, xfce4-terminal, terminator) are undetected — dispatch fails with
   "No terminal emulator found" even though a perfectly good emulator is installed.
2. There is **no multiplexer path**, so a headless `tmux`-over-SSH session (no GUI
   emulator on `PATH`) can never launch a session at all.

## Design

Everything stays inside the pure planner; the launcher is unchanged. Prompt content
remains file-indirected, the working directory stays baked into the command, and the
one-off keep-alive path is reused verbatim (the new emulators all run the same
`PosixCommand` inner string).

### 1. Generalize the exec separator to an exec *prefix*

Different emulators use different syntax for "run this command vector". A single
separator token (`-e` vs `--`) can't express all of them, so replace
`ExecSeparator(name) : string` with `ExecPrefix(name) : string[]` — the token(s)
inserted between the emulator and `bash -lc <inner>`:

| Emulator(s)                                             | Prefix        |
| ------------------------------------------------------- | ------------- |
| `gnome-terminal`                                        | `--`          |
| `konsole`, `xterm`, `alacritty`, `x-terminal-emulator`  | `-e`          |
| `xfce4-terminal`, `terminator`                          | `-x`          |
| `kitty`, `foot`                                         | *(none)*      |
| `wezterm`                                               | `start --`    |
| unknown `$TERMINAL`                                     | `-e` (default)|

The window arg vector becomes `[..prefix, "bash", "-lc", inner]`. This preserves the
existing `konsole` (`-e`) and `gnome-terminal` (`--`) shapes byte-for-byte, so every
current test still passes.

### 2. Broaden the probe list

Probe order (generic alias first, then tab-capable VTE/KDE, then modern emulators,
`xterm` as the near-last lowest-common-denominator, `terminator` last):

```
x-terminal-emulator, gnome-terminal, konsole, xfce4-terminal,
alacritty, kitty, wezterm, foot, xterm, terminator
```

Only `gnome-terminal` (`--tab`) and `konsole` (`--new-tab`) keep detection-gated tab
specs; the rest are window-only (no portable in-place tab flag). An emulator already
added via `$TERMINAL` is not probed again (dedupe by name).

### 3. tmux multiplexer path

When `$TMUX` is set **and** `tmux` is on `PATH`, emit
`tmux new-window bash -lc <inner>` (verified: tmux stops option parsing at `bash`, so
`-lc` reaches the shell intact). It is:

- a **tab analog** when a new tab was requested — tried among the tab specs (after a
  detected GUI-emulator tab, before the window fallbacks); and
- a **last-resort window fallback** otherwise — appended after the GUI emulator window
  specs, so a local GUI window is still preferred but a headless tmux session (no GUI
  emulator) still gets a working `claude` session instead of a hard failure.

## Out of scope / deferred

- Real on-hardware first-run smoke testing on macOS & Linux is tracked separately by
  [#311](https://github.com/rbcministries/clickup-todo-cli/issues/311); this PR is
  verified by unit tests plus a live check of tmux argument parsing on Linux.
- In-place tab support for emulators beyond gnome-terminal/konsole/tmux (kitty remote
  control, wezterm mux, xfce4 `--tab`) — window-only is correct and safe for now.

## Test plan

Extend `TerminalLauncherTests` (pure planner, no process spawned):

- each new emulator maps to the correct prefix and builds `[..prefix, bash, -lc, inner]`;
- the broadened probe order is respected and absent emulators are skipped;
- `$TERMINAL` is not double-added when it names a probed emulator;
- tmux: a tab request inside `$TMUX` yields a `tmux new-window` tab spec ahead of window
  fallbacks; a new-window request inside `$TMUX` appends tmux as the last fallback; no
  `$TMUX` ⇒ no tmux spec; a headless tmux session (only `tmux` present) still yields a
  runnable spec;
- prompt stays file-indirected and the baked-in `cd` rides along for the new shapes.
