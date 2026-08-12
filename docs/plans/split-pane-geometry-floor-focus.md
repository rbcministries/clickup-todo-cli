# Plan — Split-pane (C, #505): geometry, viability floor and focus policy

Slice **C** of the Split-pane epic (#502), building directly on the merged **B (#504)** planner work
(`TerminalCommandPlanner` already emits the six per-host split specs behind their in-session gates).
**Pure mapping / decision work — no launcher change, no live UI gesture** (the `Ctrl+Alt+Enter` gesture
that *requests* a split is E/F, #506/#507; this slice teaches the planner *how* the pane is shaped and
adds the pure decision for *when a split should refuse to be one*).

## Dependency note

C's only formal dependency is **B (#504)** — merged (PR #575). The **A (#503)** spike remains open, but
its unresolved half is `Ctrl+Alt+Enter` reachability, which gates the *gesture* slices (D/E, #506/#507),
**not** this pure planner/decision work; the relayout-on-split question A would inform is
["largely settled"](https://github.com/rbcministries/clickup-todo-cli/issues/503#issuecomment-5207223678)
per the owner. The focus-policy sub-decision ("A's findings on whether these actually work should drive
it") is resolved here as *best-effort per host capability*: we emit the documented stay-put token where a
host supports one and document it as unsupported elsewhere, exactly the "don't fake it" posture the issue
asks for — no live verification is needed to emit a token a host documents.

## Change 1 — geometry: express direction/size once, map per host

Two new fields on `TerminalLauncherOptions` (the record already threaded to every planner builder):

- `SplitDirection SplitDirection` — `Auto` (default), `Beside` (side-by-side, a vertical divider — the new
  pane to the right), `Below` (stacked, a horizontal divider — the new pane beneath).
- `int? SplitSizePercent` — the **new** pane's share of the parent, where the host takes a size; `null`
  (default) leaves the host's even split.

`Auto` prefers Windows Terminal's aspect-ratio auto direction (omit `-H`/`-V`) — the issue's recommended
default — and falls back to `Beside` (side-by-side) on every other host, which has no auto. **Default
options (`Auto` + `null` size) emit byte-identical argv to B**, so the existing
`TerminalCommandPlannerSplitPaneTests` stay green unchanged; geometry args appear only when set.

Per-host mapping (only the split branch of each builder changes):

| Host | Direction (`Beside` / `Below`) | Size (new pane) |
| --- | --- | --- |
| Windows Terminal (`PlanWindows`) | `-V` / `-H`; `Auto` omits both | `-s <fraction>` (e.g. `-s 0.4`) |
| tmux (`PlanLinux`) | `-h` / `-v`; `Auto`→`-h` | `-l <p>%` |
| WezTerm (`PlanLinux`) | `--right` / `--bottom`; `Auto`→`--right` | `--percent <p>` |
| kitty (`PlanLinux`) | `--location=vsplit` / `--location=hsplit`; `Auto`→`vsplit` | — (splits evenly) |
| Zellij (`PlanLinux`) | `-d right` / `-d down`; `Auto`→`right` (Zellij's `-d` is *direction*) | — (splits evenly) |
| iTerm2 (`PlanMacOS`) | `split vertically` / `split horizontally`; `Auto`→vertically | — (splits evenly) |

Size is **best-effort** — kitty/Zellij/iTerm2 take no size argument and split evenly, so `SplitSizePercent`
is silently ignored there (documented, never faked). WT's `-s` is a fraction of the parent pane; tmux/WezTerm
take a percent.

## Change 2 — viability floor: degrade split → tab below a resulting-pane-width threshold

A new **pure** evaluator `SplitViability` (I/O-free, unit-tested — the planner has no terminal, so the floor
decision is made by the *caller* before it builds launcher options, and fed back as a possibly-degraded
`LaunchLocation`):

```
SplitViability.Evaluate(int terminalColumns, SplitDirection direction, int? sizePercent = null,
                        int minPaneColumns = DefaultMinPaneColumns) -> Decision
Decision(LaunchLocation Location, bool Degraded, int ResultingColumns, string? Reason)
```

- **Threshold on the *resulting* pane width, not the current one.** For a `Below` (stacked) split the
  columns are unchanged — only rows shrink — so a stack never trips the floor (it's a *column* threshold).
  For `Beside`/`Auto` the columns divide: the new pane gets `sizePercent` (default even), our pane keeps the
  rest; **both host a TUI, so the smaller of the two is the binding width** (`min(newCols, ourCols)`).
- **Derived, not invented.** `DefaultMinPaneColumns = 60`, derived from the task list's fixed leading chrome
  — the id chip + the four-column Status abbreviation gutter (`TaskRowFormatter.StatusGutter`) + the
  three-column Priority gutter (`BlankGutter`) + the fold marker ≈ 18 columns before the title even starts —
  plus a ~40-column readable title ≈ 58, rounded to **60**, which is also the width the issue itself names
  ("two 60-column panes are worse than one tab"). It's a single tunable constant / an `Evaluate` parameter,
  so the maintainer can move it in one place.
- **Configurable, sensible default unattended.** The floor is a parameter (`minPaneColumns`) defaulting to
  the derived constant; the caller can pass a user-configured value. Surfacing that value as a user-facing
  setting rides on the settings surface the gesture slices add — deferred to **#507 / #508 / #511** (which
  own the split gesture, the dispatch-destination setting, and the validation matrix respectively); the
  mechanism and default land here, fully tested.
- **Legible degradation.** When it triggers, `Decision.Reason` is a ready-to-flash string naming the
  resulting width and the floor, so the status line can say *why* it opened a tab rather than looking like
  the split silently failed. The gesture wiring that flashes it is E/F/J.

## Change 3 — focus policy: best-effort "stay put" per host capability

A new field `SplitFocus SplitFocus` on `TerminalLauncherOptions` — `FollowPane` (default; today's behaviour,
the host moves focus to the new pane) or `StayPut`. `StayPut` appends the documented focus-retention token
**only** on the hosts that support one, and is a no-op (documented unsupported) elsewhere:

| Host | `StayPut` token | Supported? |
| --- | --- | --- |
| Windows Terminal | chain a `; mf previous` subcommand (bounces focus back to our pane) | ✅ |
| tmux | `-d` on `split-window` (don't make the new pane active) | ✅ |
| kitty | `--dont-take-focus` on `kitten @ launch` | ✅ |
| WezTerm / Zellij / iTerm2 | — | ❌ documented unsupported (focus follows) |

**WT delimiter care (#534):** the `mf previous` retention is a *separate* `wt` subcommand chained with a
literal `;`. `WtArgs` escapes `;` inside command arguments (so the payload isn't torn), so the focus tokens
(`";"`, `"mf"`, `"previous"`) are appended **after** the escape pass — the `;` is a structural separator,
never user data, so it must stay unescaped.

**Policy decision (the issue asks C to make it):** focus policy is driven by **launch intent**, not a user
setting — the caller passes `StayPut` for an ambient `--feed` sidebar and `FollowPane` (default) for
`--task`/`--chat`/dispatch, where the user wants to interact with what they just opened. This keeps the
knob where the intent is known (the gesture) rather than adding a global setting that can't know which host
is opening. Default `FollowPane` preserves B byte-for-byte.

## Out of scope (deferred, tracked under epic #502)

- The `Ctrl+Alt+Enter` chord / the `OpenInSplitPane` gesture and any UI — **D/E (#506/#507)**.
- Wiring geometry/floor/focus into a user-facing settings surface — **#507 / #508 / #511**.
- Dispatch-into-a-pane launch path and `--feed`/`--chat` hosts — **F/H/J (#508/#510/#515)**.
- Cross-platform live validation on real terminals — **I (#511)**.

No launcher change (`TerminalLauncher` untouched), no `Generated/` edit, no `clickup-openapi.json` change,
no Kiota regen.

## Tests

Pure, so every branch is covered without a terminal:

- **Geometry** (extend `TerminalCommandPlannerSplitPaneTests`): per-host direction argv for `Beside`/`Below`
  and `Auto`; per-host size argv where taken (WT fraction, tmux `%`, WezTerm `--percent`) and ignored where
  not (kitty/Zellij/iTerm2); default options emit the exact B argv (regression pin).
- **Focus** (same suite): `StayPut` emits the retention token on WT/tmux/kitty (WT after the escape pass,
  with the literal `;`), and is a no-op on WezTerm/Zellij/iTerm2; `FollowPane` byte-identical to B.
- **Viability floor** (`SplitViabilityTests`): the floor triggering and its degraded location + reason; a
  `Below` split never trips; the `min(newCols, ourCols)` binding under an uneven `sizePercent`; the default
  constant; a custom `minPaneColumns`; and that a degraded decision feeds the planner a `NewTab` request
  that yields exactly today's tab specs (the "degraded spec").
