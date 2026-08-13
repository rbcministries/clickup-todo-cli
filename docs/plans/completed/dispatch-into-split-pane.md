# Plan — Split-pane (J, #515): dispatch into a split pane

Slice **J** of the Split-pane epic (#502). Depends on **B (#504)** — merged, the
`LaunchLocation.SplitPane` planner routing — and **C (#505)** — merged, `SplitViability`,
geometry and focus policy. Pairs with **F (#508)** — merged, which exposed the *choice* of a
split-pane destination in Settings and the Dispatch pane. This slice makes the launch **actually
correct once a dispatch chooses a pane**: the working-directory semantics, the one-off gate, the
`--duplicate` prohibition, the viability floor for repeated dispatch, and pane lifetime.

It does **not** depend on the still-open spike **A (#503)** — its open half (`Ctrl+Alt+Enter`
reachability) gates the *gesture* slices D/E (#506/#507), not this dispatch launch path, which is
driven from the existing Dispatch pane control shipped in F.

## Verified current state (`main`)

- `TerminalCommandPlanner` already routes `LaunchLocation.SplitPane` for every host and never emits
  `wt --duplicate`/`-D` — the working directory is baked **into the command** (`Set-Location
  -LiteralPath <dir>` on Windows, `cd '<dir>' &&` on POSIX) and the split specs run that same
  payload. So the #461 project-directory landing already works via the command prefix; this slice
  pins it with tests.
- The one-off gate already holds: `SplitRequested`/`TabRungRequested` both require `!oneOff`, and
  `DispatchPaneModel.LaunchLocationApplies` greys the control out in one-off mode. A `claude -p` run
  goes through the background runner with no terminal, so panes stay meaningless there. This slice
  pins it with a test.
- `SplitViability.Evaluate` (#505) is pure and unit-tested, but **no caller invokes it** — its own
  doc says "the caller (the split gesture, epic #502 E/F/J) supplies the live terminal width". This
  slice (**J**) is that caller for the dispatch path.
- Pane lifetime differs by platform today: a Windows dispatch pane persists (the PowerShell host
  launches `-NoExit`); a POSIX dispatch pane's `bash -lc` shell exits when `claude` exits and the
  pane closes — triggering a relayout of the remaining panes. #515 flags this as something to decide
  deliberately, "silently differing by platform is not [defensible]".

## Design

### 1. Viability floor wiring (the headline: "panes accumulate")

Dispatch is repeatable, so each dispatch subdivides a finite width; the fourth dispatch from one
window is a fifth pane and unusable. `SplitViability.Evaluate` already decides this on the
**resulting** pane width; it just needs the live terminal width fed in and the (possibly degraded)
location fed back to the planner — decided **before** planning because the planner has no notion of
terminal size.

The decision belongs in `DispatchCoordinator.Plan`, which is already the single pure resolution
seam both hosts share and is fully unit-tested:

- **`Plan` gains an optional `int? terminalColumns = null`.** When it is `null` (every pre-#515
  caller and every existing test) nothing changes — the launch location passes through untouched, so
  `Plan` stays byte-identical for non-split and width-unknown paths.
- When `request.LaunchLocation == SplitPane`, the session is interactive (`!oneOff`), **and**
  `terminalColumns` is supplied, `Plan` calls `SplitViability.Evaluate(cols, direction, sizePercent)`
  with the geometry projected from settings (`ToLauncherOptions().SplitDirection`/`SplitSizePercent`
  — today `Auto`/`null`, so an even side-by-side split; future-proof when the geometry settings
  surface lands). The returned `Decision.Location` becomes the effective
  `ResolvedDispatch.LaunchLocation`, and `Decision.Reason` is carried on a new
  `ResolvedDispatch.SplitDegradedReason` (null unless it degraded).
- A one-off dispatch never evaluates (it has no terminal); a `NewTab`/`NewWindow` dispatch never
  evaluates. So the floor only ever downgrades an interactive `SplitPane` → `NewTab`, never the
  reverse.

The default floor (`SplitViability.DefaultMinPaneColumns = 60`) is used as-is; surfacing it as a
user setting rides on the settings surface deferred to #511 (C already made it a parameter).

### 2. Working directory + no `--duplicate` (#461)

No code change — the mechanism is already right (command-prefix `cd`/`Set-Location`, never `-D`).
This slice **pins** it with planner tests: the dispatch split spec for each host carries the
repo-matched directory in its payload and no spec ever contains `--duplicate` or a bare `-D`. The
`resolvedDefault` reconciliation (#96/#461) is unchanged and computed once in the host, exactly as
today — the pane destination must not introduce a second place the directory is computed, which
`Plan`'s viability block respects (it reads `request.LaunchLocation`, never recomputes the dir).

### 3. Pane lifetime — decision: **persist on both platforms**

Windows already persists via `-NoExit`. To stop a POSIX dispatch pane vanishing (and relayouting
the survivors) the instant `claude` exits — and to remove the silent platform difference #515 calls
out — an **interactive dispatch launched into an actual split pane keeps the POSIX shell alive**,
mirroring Windows. Rationale: you dispatched an agent *beside* the task to watch what it does; the
pane holding its final output shouldn't disappear on its own.

Implementation is scoped to the **actual POSIX split-branch payloads** (tmux / WezTerm / kitty /
Zellij / iTerm2), not the shared inner command, so the existing invariant that a `SplitPane` request
on a *pane-incapable* host degrades to **exactly** a `NewTab`'s specs is preserved — a degraded
request uses the untouched shared payload and gets no keep-alive (its tab/window lifetime is the
pre-existing behaviour for all tabs/windows, out of scope here). The keep-alive reuses the existing
one-off idiom (a `printf` + `read` prompt) with interactive wording. This is additive: the Windows
path is unchanged, and non-split launches are unchanged.

### 4. Hosts

`TodoApp.DispatchAgent` and `SingleTaskApp.DispatchAgent` pass the live terminal width
(`Application.Driver?.Cols` — `null` in a headless/unit context, so viability self-disables) into
`Plan`. `DispatchCoordinator.RunInteractive` prepends `plan.SplitDegradedReason` (when set) to the
status it flashes, so a dispatch that opened a tab instead of a pane reads as deliberate rather than
a silently-ignored choice.

## Tests (pure / offline)

- **`TerminalCommandPlannerSplitPaneTests`** (extend):
  - the dispatch split spec on each host carries the working directory in its payload (`cd '<dir>'`
    / `Set-Location … '<dir>'`), given a repo-matched `cwd`;
  - **no** spec on any host ever contains `--duplicate` or a standalone `-D`;
  - the POSIX split payload carries the interactive keep-alive; a `SplitPane` request degraded on a
    pane-incapable host (gnome-terminal) still equals the `NewTab` specs exactly (no keep-alive on
    the degraded rung — the existing invariant, re-asserted);
  - the one-off gate still suppresses split/tab (already covered; keep).
- **`DispatchCoordinatorTests`** (extend):
  - a wide terminal keeps a `SplitPane` dispatch a split (`LaunchLocation` unchanged, no reason);
  - a narrow terminal degrades it to `NewTab` with a non-null `SplitDegradedReason`;
  - a repeated/narrow pane (small `terminalColumns`) degrades — the "panes accumulate" case;
  - `null` `terminalColumns` leaves `SplitPane` untouched (byte-identical to pre-#515);
  - one-off + `SplitPane` never evaluates (no terminal); `NewTab`/`NewWindow` never degrade;
  - the viability path does not perturb `ChosenDir`/directory resolution (the #461 guard).
- **`SplitViabilityTests`** already cover the floor arithmetic (#505) — reused, not duplicated.

## Hard-rule compliance

- No `Generated/` edits, no `clickup-openapi.json` change, no Kiota regen (no ClickUp API surface).
- ClickUp auth quirk untouched.
- No new focusable pane and no new keybinding (#3/#12) — the destination control is the existing
  Dispatch-pane control from F (#508); this slice only makes its `SplitPane` value launch correctly.
- New units are pure/offline; no integration surface, so no new `SkippableFact`.
- TUI wiring (the two host call sites, `RunInteractive`'s flash) isn't CI-unit-testable — verified by
  build + reasoning and described in the PR; the pure decision it feeds is fully unit-tested.

## Out of scope (tracked under epic #502)

- The `Ctrl+Alt+Enter` gesture / `OpenInSplitPane` and the app-host (`--task`/`--feed`) pane
  gestures — D/E (#506/#507).
- Surfacing the viability floor as a user setting — #511.
- WT profile composition with the split form — K (#516).
- The coherent dispatch status line (destination + dir + profile) — L (#517).
- Cross-platform live validation on real terminals — I (#511).
