# Plan — Split-pane (B, #504): `LaunchLocation.SplitPane` in the planner + the split→tab→window ladder

Slice **B** of the Split-pane epic (#502). **Pure planner work — no launcher change, no UI.**
The `Ctrl+Alt+Enter` gesture that will *request* a split is deferred to **E/F**; this slice only teaches
`TerminalCommandPlanner` to *emit* the per-host split candidates so a later `LaunchLocation.SplitPane`
request has something to launch.

## Dependency note (the "Depends on A")

#504's body says "Depends on **A**" (the #503 spike). Per the owner's recorded
[#503 finding](https://github.com/rbcministries/clickup-todo-cli/issues/503#issuecomment-5207223678),
the remaining spike gate — whether `Ctrl+Alt+Enter` reaches `OnKey` across drivers/hosts — is "the reason
#503 still blocks **#506 and #507**" (the chord/gesture slices), **not** this pure planner work. The
relayout-on-split question that *would* affect the planner's output is "largely settled" (the redraw
already absorbs a manual WT split, and a programmatic `wt … sp` is indistinguishable from a manual one).
The per-host split argv specs are fully written out in #504's table, so the planner can faithfully encode
them now and is independently unit-testable without a terminal. If the spike later tweaks a command it is a
one-line change to a single spec.

## Change 1 — `LaunchLocation.SplitPane`

`LaunchLocation` (`Agent/TerminalLauncherOptions.cs`) gains a third value, `SplitPane`, joining
`NewWindow` and `NewTab`. Documented as the in-place split destination; interactive-only (a one-off
`claude -p` run has no terminal, so it never splits — same rule as `NewTab`).

## Change 2 — per-host split branches (gated) + the ladder

Each per-OS builder in `TerminalCommandPlanner` gains a split branch behind its own **in-session**
detection gate, mirroring how the existing tab specs are gated by `NewTabRequested` + a per-emulator env
probe. Geometry (direction/size) and the viability floor are **out of scope (slice C)** — B emits the
minimal split spec; C adds `-s`/`--percent`/`-l %` and the too-narrow-degrades-to-tab floor.

| Host | OS builder | Spec (B, minimal) | Gate (env + exe) | DisplayName |
| --- | --- | --- | --- | --- |
| Windows Terminal | `PlanWindows` | `wt -w 0 sp pwsh -NoExit -Command <cmd>` | `WT_SESSION` + `wt` | `Windows Terminal (split pane)` |
| tmux | `PlanLinux` | `tmux split-window -h bash -lc <inner>` | `TMUX` + `tmux` | `tmux (split pane)` |
| WezTerm | `PlanLinux` | `wezterm cli split-pane --right -- bash -lc <inner>` | `WEZTERM_PANE` + `wezterm` | `WezTerm (split pane)` |
| kitty | `PlanLinux` | `kitten @ launch --location=vsplit --cwd=current bash -lc <inner>` | `KITTY_LISTEN_ON` + `kitten` | `kitty (split pane)` |
| Zellij | `PlanLinux` | `zellij action new-pane -d right -- bash -lc <inner>` | `ZELLIJ` + `zellij` | `Zellij (split pane)` |
| iTerm2 | `PlanMacOS` | `osascript` split of `current session of current window`, then `write text` | `TERM_PROGRAM=iTerm.app` + `osascript` | `iTerm2 (split pane)` |

Notes:
- **kitty's gate is deliberately honest:** `KITTY_LISTEN_ON` is only set when `allow_remote_control` is
  enabled, so gating on it probes the *capability*, not merely that kitty is running. The split runs
  through the `kitten` binary (`kitten @ launch`), so its presence is the exe gate.
- **WezTerm, kitty and Zellij become in-place-capable for the first time** — WezTerm was window-only in
  `LinuxEmulators`; kitty/Zellij weren't in the matrix at all. Their *window/tab* handling is unchanged;
  this only adds a split rung.
- **Hosts with no pane concept** (gnome-terminal, konsole, xfce4-terminal, Alacritty, foot, xterm,
  terminator, Terminal.app) emit no split spec and so degrade automatically. **Konsole and Ghostty are
  explicit non-goals** for the split rung (Konsole's `--new-tab` has no scriptable split; Ghostty has no
  split CLI).

### The degradation ladder

Candidate order becomes `[custom?, ..splitSpecs, ..tabSpecs, ..windowSpecs]`, so **split → tab → window**
falls out of the existing ordering rather than needing new control flow — the same reasoning that already
puts tab specs ahead of window specs (the launcher starts candidates in order and stops at the first that
starts, so a generic window candidate ordered first would silently preempt a detected in-place one).

Gating predicates:
- `splitSpecs` emitted iff **`SplitRequested`** (`LaunchLocation == SplitPane && !oneOff`).
- `tabSpecs` emitted iff **`TabRungRequested`** (`LaunchLocation is NewTab or SplitPane && !oneOff`) — a
  split request ladders down through the tab rung, so `NewTabRequested` widens to include `SplitPane`
  (renamed `TabRungRequested`). A plain `NewTab` request is unchanged; `NewWindow` still emits neither.
- `windowSpecs` always emitted (the base fallback).

So a `SplitPane` request inside a split-capable host yields `[split, tab?, ..windows]`; the same request
in a pane-incapable host yields **exactly today's** tab/window specs.

## Change 3 — generalise `AppTabLaunch` → `AppHostLaunch`

`AppTabLaunch` (the pure options + status-string helper shared by the dashboard / single-task / feed
`Ctrl+Enter`) is renamed `AppHostLaunch`; `Options`/`Opening`/`Opened`/`Fallback` take the
`LaunchLocation` **destination** rather than hard-coding `NewTab`, and the status strings stop saying
"tab" unconditionally (destination-aware wording: `new terminal tab` / `new terminal window` /
`split pane`). The three current callers (`TodoApp`, `SingleTaskApp`, `FeedApp`) still pass
`LaunchLocation.NewTab` — behaviour is byte-identical today; the split gesture (E/F) will pass
`SplitPane`. The `NewTab` wording is preserved exactly so existing status strings don't regress.

## Out of scope

Geometry / viability floor (**C**), the `Ctrl+Alt+Enter` chord and any UI (**D/E/F**), macOS WezTerm/kitty
splits (the macOS builder is osascript-centric today; those stay Linux-only this slice), Konsole/Ghostty.
No launcher change (`TerminalLauncher` is untouched); no `Generated/` or spec change.

## Tests — `TerminalCommandPlannerSplitPaneTests`

Alongside the existing planner suites, all runnable without a terminal (the planner is pure):
- Per-host spec shape (argv + DisplayName) for WT, tmux, WezTerm, kitty, Zellij, iTerm2.
- Each detection gate present **and** absent (no env ⇒ no split spec; env present ⇒ split spec).
- The full ladder ordering for a `SplitPane` request on a split-capable host (`split, tab, window`).
- A `SplitPane` request on a pane-incapable host produces **exactly** today's tab/window specs.
- `NewTab` / `NewWindow` requests emit **no** split specs (no regression).
- The `AppHostLaunch` rename: destination-aware wording for each `LaunchLocation`, `NewTab` byte-identical
  to the retired `AppTabLaunch` strings.
