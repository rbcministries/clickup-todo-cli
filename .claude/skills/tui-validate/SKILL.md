---
name: tui-validate
description: Validate TUI rendering, colors, and keypress latency end-to-end by running the real app under a PTY against a fake ClickUp backend, asserting on a pyte-emulated screen. Use for changes that touch rendering, the list source, drivers/output, or keypress handling — and only after `dotnet test` is fully green (see CLAUDE.md).
---

# TUI end-to-end validation

Runs the **real** `TodoApp` (real Terminal.Gui stack, no network) under a pseudo-terminal,
drives it with keyboard escape sequences, and validates what a user would actually see by
feeding all output through a real VT emulator (pyte). Everything needed lives in the harness
project at `tests/ClickUpTodo.Tui.E2E/` — do not rebuild this from scratch; it encodes
several hard-won fixes (see Pitfalls).

The harness host (`Program.cs`) and its Python checks live in the test tree, **not** in this
skill, so adding a scenario is an ordinary edit under `tests/` — no skill-directory writes.
This file is the how-to; the code it drives is versioned alongside the app it exercises.

## Prerequisites

```bash
pip install pyte                      # VT emulator for screen/color assertions
dotnet build -c Release tests/ClickUpTodo.Tui.E2E/ClickUpTodo.Tui.E2E.csproj
```

`DLL=tests/ClickUpTodo.Tui.E2E/bin/Release/net10.0/ClickUpTodo.Tui.E2E.dll` below.
The harness (`tests/ClickUpTodo.Tui.E2E/Program.cs`) boots the app against a canned in-process ClickUp
backend (a fake `HttpMessageHandler` — no sockets). Scenario knobs (env vars):

- `E2E_TASKS=200` — task count (paging is exercised above 100)
- `E2E_VIEW=rich` — grouping by list + F4 subtasks + pinned tasks
- `E2E_REFRESH=600` — background poll interval; keep high so it stays out of timings
- `CLICKUP_TODO_NO_DIFF=1` — app escape hatch; doubles as the **stock-renderer baseline** for A/B
- `E2E_WARM_CLOSED=1` — warm the closed-task cache before boot (a real `PrefetchClosedTasksAsync`)
  so the F12→All bridge has a set to splice, and serve the closed task with a recent `date_updated`
  so it survives the cache's age window (#333). Off ⇒ empty warm set, original fixed date.
- `E2E_STALL_CLOSED_MS=<ms>` — delay the authoritative `include_closed=true` refresh (the F12→All
  fetch, once the app has booted) by this many ms, so the pre-refresh bridge frame is observable
  before the superset lands (#333). The pre-boot warm prefetch runs unstalled.

## Checks (each is one command; all exit nonzero / print a traceback on failure)

**1. Keypress latency** — time from sending `↓` to the redraw arriving:

```bash
E2E_TASKS=200 timeout 90 python3 -u tests/ClickUpTodo.Tui.E2E/drive.py $DLL 10
```

Baseline: ~50 ms median locally (dominated by the driver's 20 ms input poll + 25 fps
iteration cap). Investigate anything over ~150 ms sustained.

**2. Output volume + visible screen** — bytes emitted per keypress and a dump of the
final rendered screen text:

```bash
E2E_TASKS=200 timeout 40 python3 -u tests/ClickUpTodo.Tui.E2E/screen_check.py $DLL 5 /tmp/screen.txt
```

Baseline: **~0.9 KB per Down-press** with the diff-flush output (default); the stock
renderer (`CLICKUP_TODO_NO_DIFF=1`) re-sends the whole viewport at ~18.5 KB. A large
regression here means unchanged cells are being re-flushed again.

**3. Rendering correctness incl. colors (A/B vs stock)** — scripted session
(boot → 3×`↓` → F1 help → Esc → `↓`), then a per-cell `char|fg|bg|reverse` signature dump:

```bash
E2E_TASKS=200 timeout 60 python3 -u tests/ClickUpTodo.Tui.E2E/color_check.py $DLL /tmp/cells_new.txt
E2E_TASKS=200 CLICKUP_TODO_NO_DIFF=1 timeout 60 python3 -u tests/ClickUpTodo.Tui.E2E/color_check.py $DLL /tmp/cells_stock.txt
diff /tmp/cells_stock.txt /tmp/cells_new.txt
```

Expected: identical except the status-line row containing the wall-clock timestamp
(one differing row is a pass; mask it rather than loosening the comparison).
`DO_RESIZE=1` adds a mid-session terminal resize — use it as a **sanity check only**
(borders/rows intact), never as an equality A/B: resize timing races make scroll
position legitimately nondeterministic between runs.

**4. Detail screen + tab switching (A/B vs stock)** — Enter opens the detail view, then
Tab cycles Description/Comments/Other; the fake backend seeds comments with emoji,
a VS16 sequence (🛠️), em-dashes, curly quotes, and a URL on the same lines — the exact
grapheme mix that exposed sparse-flush cursor drift in the field:

```bash
E2E_TASKS=20 timeout 60 python3 -u tests/ClickUpTodo.Tui.E2E/detail_check.py $DLL /tmp/detail_new.txt
E2E_TASKS=20 CLICKUP_TODO_NO_DIFF=1 timeout 60 python3 -u tests/ClickUpTodo.Tui.E2E/detail_check.py $DLL /tmp/detail_stock.txt
diff /tmp/detail_stock.txt /tmp/detail_new.txt
```

Expected: identical. A `▯` in the dump marks an orphaned half of a wide glyph
(pyte's own `screen.display` crashes on those) — any `▯`, doubled letters, or
border-column shifts (`││C…`) mean flushed runs are landing at wrong columns.
This check is why the frame diff is **row-atomic**: cell-level skipping repositions
the cursor mid-row from the buffer's column model, which drifts around
wide/ambiguous-width graphemes; whole-row flushes are byte-identical to stock.

**5. Instant closed-task bridge paint (#253/#280) — A/B isolating the bridge** — F12→All
splices the warm closed set into the snapshot and paints it *before* the authoritative
`include_closed=true` refresh returns. This asserts that pre-refresh frame by warming the
cache (`E2E_WARM_CLOSED`) and stalling the refresh (`E2E_STALL_CLOSED_MS`) so the closed row
is observable while the refresh is still blocked; a control leg (no warm) confirms the row is
absent during the stall and appears only after it — proving the row is the bridge, not the
refresh firing early:

```bash
timeout 90 python3 -u tests/ClickUpTodo.Tui.E2E/closed_bridge_check.py $DLL
```

Self-contained (drives both legs, sets its own env). Expected: `ok — F12→All paints the warm
closed set on the pre-refresh frame …`.

**6. In-text link styling (#317)** — opens Task Detail and asserts, on the pyte screen, the two
link styles rendered in the panes: the seeded ClickUp **task** link (Description) is underlined and
keeps the normal body foreground; the seeded **web** link (Comments) is underlined and recoloured
(uniform across the URL). Relational assertions (recoloured-vs-not, underlined) so it's robust to
pyte's colour encoding; the unit tests pin the concrete attributes:

```bash
E2E_TASKS=20 timeout 90 python3 -u tests/ClickUpTodo.Tui.E2E/link_check.py $DLL
```

Self-contained (fixed COLS=120 so the seeded URLs don't wrap). Expected: `ok — task link
underlined+default-fg (Description), web link underlined+recoloured (Comments)`. The colour/underline
change is invisible to the text-only `detail_check.py` A/B, which stays identical.

**7. Task Detail tab-boundary crash guard** — Terminal.Gui 2.4.10's stock `Tabs` control crashes
(`InvalidOperationException: FocusChanging was not cancelled …` in `Tabs.SelectNextTab`/
`SelectPreviousTab`) when a bare arrow drives tab navigation past the first/last tab; the app disables
that native navigation with `NavSafeTabs`. This opens Task Detail, cycles to the Task Tree tab, and
drives bare → past the last tab and bare ↑ past its top row (the ListView reliably bubbles the arrow to
the tab control), asserting the app stays alive and never leaves the tab. Requires `E2E_TREE=1` so the
Task Tree tab is present as the last tab:

```bash
E2E_TASKS=20 E2E_TREE=1 timeout 120 python3 -u tests/ClickUpTodo.Tui.E2E/tab_boundary_check.py $DLL
```

Self-contained (sets `E2E_TREE=1` itself). Expected: `ok — survived bare arrow navigation past both tab
boundaries; ↑ on the Task Tree tab stays on the tab`. Reproduces the crash on the stock control (revert
`NavSafeTabs`→`Tabs` to confirm) and passes on the fix.

**8. Threaded comments render nested (#329)** — opens Task Detail, cycles to the Comments tab, and
asserts on the pyte screen that a comment's reply thread renders **indented** under its parent, not
flat. Two legs: with `E2E_THREADS=1` the fake backend marks comment `c2` with a two-reply thread and
serves `GET /comment/c2/reply`, so the real `CommentThreadLoader` fetches the replies and the
formatter indents them (asserts an indented reply-marker line `^\s+↳` — measured after stripping the
pane's box-drawing border — plus both reply bodies, with the parent at the pane's left margin); the
control leg (no `E2E_THREADS`) asserts the marker and reply bodies are absent, proving the nesting is
driven by loaded thread data:

```bash
timeout 150 python3 -u tests/ClickUpTodo.Tui.E2E/thread_check.py $DLL
```

Self-contained (drives both legs, sets its own env). Expected: `ok — threaded leg nests N indented
reply line(s) … control leg has no marker and no replies`.

## Pitfalls (violating these produced false "the TUI can't be tested" conclusions)

- **Answer the terminal's queries or nothing ever renders.** Terminal.Gui's ANSI driver
  asks the terminal for size (`ESC[18t`) and cursor position (`ESC[6n`); the scripts'
  `answer_queries()` replies on every read. A dumb pipe that never answers = permanently
  blank app.
- **Assert on the pyte screen, never on raw output bytes.** With diffed flushing, text
  reaches the terminal as fragments interleaved with cursor moves — `b"Task 1"` may never
  appear contiguously in the byte stream even though the screen is perfect. Raw bytes are
  only valid for *volume* metrics.
- **"First output byte" is not latency.** The app emits idle chatter every 40 ms
  iteration (cursor hide/home, periodic size query). Measure to a chunk containing row
  content; subtract the idle-window byte count from volume numbers.
- **Kill stale harness processes before rebuilding** — a running instance holds the DLL
  and `dotnet build` silently leaves the old binary in place. Run `pkill -f '[C]lickUpTodo.Tui.E2E.dll'`
  as its **own** shell command: chained into a compound command whose later arguments
  mention the DLL name, `pkill -f` matches the shell's own command line and kills it
  (exit 144). (The `[C]…` bracket keeps the pattern from matching the `pkill` line itself.)
- Failure dumps show the *last* 1500 bytes, which is always idle chatter — read the whole
  captured stream (or the pyte screen) before concluding the app rendered nothing.

## Not covered (still needs a human pass)

Real terminal-emulator quirks (xterm/Windows Terminal resize reflow, emulator-specific
wide-glyph rendering), mouse input, and the `windows`/`dotnet` drivers (harness drives
the default `ansi` driver).
