# Launch ladder: honour `NewTab` inside WezTerm / kitty / Zellij

Issue: #589 · Part of the Split-pane epic #502 · Surfaced reviewing PR #588 (#515).

## Problem

`TerminalCommandPlanner.PlanLinux` only emits a real **tab** rung for a `NewTab`
request on three hosts: gnome-terminal (`--tab`), konsole (`--new-tab`), and
tmux (`new-window`). The modern in-place hosts **WezTerm**, **kitty** and
**Zellij** have a scriptable split rung (added in #504/#505) but **no tab rung**,
so a `NewTab` request inside them falls through:

- **WezTerm / kitty** — both are in `LinuxEmulators`, so a `NewTab` request opens
  a detached **window** instead of a tab in the current window.
- **Zellij-only** session (no tmux, no GUI emulator on `PATH`, and Zellij is
  *not* in `LinuxEmulators`) — the request produces **no candidate at all** →
  an empty spec list → the dispatch silently fails to launch.

This was always latent (a user picking "New tab" inside these hosts already hit
it), but #505's viability floor + #515 newly route users here: a `SplitPane`
dispatch in a terminal too narrow to split degrades to `NewTab`
(`SplitViability.Evaluate`), so a narrow WezTerm/kitty session now opens a window
and a narrow Zellij-only session now fails outright. The floor's degradation
message also hard-codes "…opened a tab instead", which is wrong on any host
where the fallback isn't a tab.

## Acceptance criteria (from the issue)

1. A `NewTab` request inside WezTerm/kitty/Zellij opens a tab **in that host**
   (not a new window), gated per the in-session probe; a Zellij-only session
   always yields **at least one** launch candidate.
2. The floor-degraded dispatch message doesn't claim "tab" on hosts where the
   fallback isn't a tab.
3. Pure planner unit tests (mirroring `TerminalCommandPlannerSplitPaneTests`); no
   `Generated/` edits.

## Approach

### 1. Add in-place tab rungs to `PlanLinux` (mirrors the split rungs)

Each rung is gated on the **same in-session env probe** as that host's split
rung, plus its executable, and fires only when a tab was requested — i.e.
`tab = TabRungRequested(options, oneOff)`, which is already true for an explicit
`NewTab` **and** for a `SplitPane` that degrades down the split → tab → window
ladder. The tab specs are appended to the existing `tabSpecs` list, so they land
ahead of the window fallback (the planner returns
`[..splitSpecs, ..tabSpecs, ..windowSpecs]`).

| Host    | Probe              | Exe       | Tab argv                                                    |
|---------|--------------------|-----------|-------------------------------------------------------------|
| WezTerm | `WEZTERM_PANE`     | `wezterm` | `cli spawn -- bash -lc <inner>`                             |
| kitty   | `KITTY_LISTEN_ON`  | `kitten`  | `@ launch --type=tab --cwd=current bash -lc <inner>`       |
| Zellij  | `ZELLIJ`           | `zellij`  | `action new-pane -- bash -lc <inner>`                      |

- `wezterm cli spawn` (no `--new-window`) opens a **new tab in the current
  window**; the `--` fences the command like the split rung's `cli split-pane`.
- `kitten @ launch --type=tab` mirrors the split rung's `--location=vsplit`
  form (same `--cwd=current` + bare `bash -lc <inner>`).
- **Zellij** has no window concept, and — unlike tmux's `new-window` — its
  `action new-tab` **cannot carry a command** (only a layout can, and the
  planner writes no files). So Zellij's honest in-session surface is a **new
  pane** (`action new-pane`, no `-d` direction — distinct from the directional
  split rung). Labelled `Zellij (new pane)` so the flash/label stays truthful.

### 2. Zellij last-resort fallback (guarantees a non-empty candidate list)

Because Zellij has no GUI window fallback in `LinuxEmulators`, its new-pane rung
does **double duty** exactly like tmux's `new-window`: emitted when the host is
detected regardless of the request — routed to `tabSpecs` when a tab was asked
for, else appended to `windowSpecs` as the last-resort candidate. This is what
guarantees a Zellij-only session (any launch location, including one-off) always
yields ≥1 candidate. WezTerm/kitty keep their existing `LinuxEmulators` window
spec as the fallback, so their rung is tab-only.

### 3. Host-agnostic degradation message (`SplitViability`)

`SplitViability.Evaluate` is pure and host-unaware; its `Decision.Location`
stays `NewTab` (the planner decides what `NewTab` becomes per host). Only the
user-facing `Reason` string changes, dropping the "opened a tab" over-promise:

> `Terminal too narrow to split (<n>-column panes; need <floor>) — opening elsewhere instead.`

## Test plan

- **New (`TerminalLauncherTests`, NewTab section):** WezTerm spawns a tab ahead
  of its window fallback / falls back to window when not detected; kitty launches
  a `--type=tab` via `kitten` when `KITTY_LISTEN_ON` set / falls back to window
  without remote-control or without `kitten`; Zellij opens an in-session pane
  when `ZELLIJ` set; the working directory is baked into each new command.
- **New:** a Zellij-only session yields ≥1 candidate for **every** launch
  location (Window / Tab / Split) — pins AC1's non-empty guarantee.
- **Updated (`TerminalCommandPlannerSplitPaneTests`):** the split → tab → window
  ladder inside WezTerm and kitty now includes the tab rung; Zellij split now
  emits the split rung **then** its in-session pane fallback (mirrors the
  existing tmux `split → new-window` test).
- **Updated (`SplitViabilityTests`):** the degradation reason is host-agnostic —
  keeps the width numbers, no longer claims a "tab".

## Hard-rules check

- No `Generated/` edits, no `clickup-openapi.json` change, no Kiota regen (no
  ClickUp API surface).
- Pure planner logic + one string; fully unit-tested, no integration surface.
- No TUI/rendering change, no new focusable pane, no new keybinding.

## Deferred (tracked under epic #502)

- Live cross-platform validation of the new rungs on real WezTerm/kitty/Zellij
  sessions — the cross-platform validation matrix, #511.
