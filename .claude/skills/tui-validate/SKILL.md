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

**`drive.py`** — keypress latency: time from sending `↓` to the redraw arriving:

```bash
E2E_TASKS=200 timeout 90 python3 -u tests/ClickUpTodo.Tui.E2E/drive.py $DLL 10
```

Baseline: ~50 ms median locally (dominated by the driver's 20 ms input poll + 25 fps
iteration cap). Investigate anything over ~150 ms sustained.

**`screen_check.py`** — output volume + visible screen: bytes emitted per keypress and a dump of the
final rendered screen text:

```bash
E2E_TASKS=200 timeout 40 python3 -u tests/ClickUpTodo.Tui.E2E/screen_check.py $DLL 5 /tmp/screen.txt
```

Baseline: **~0.9 KB per Down-press** with the diff-flush output (default); the stock
renderer (`CLICKUP_TODO_NO_DIFF=1`) re-sends the whole viewport at ~18.5 KB. A large
regression here means unchanged cells are being re-flushed again.

**`color_check.py`** — rendering correctness incl. colors (A/B vs stock): scripted session
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

**`detail_check.py`** — detail screen + tab switching (A/B vs stock): Enter opens the detail view, then
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

**`closed_bridge_check.py`** — instant closed-task bridge paint (#253/#280), A/B isolating the bridge: F12→All
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

**`link_check.py`** — in-text link styling (#317): opens Task Detail and asserts, on the pyte screen, the two
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

**`link_wrap_check.py`** — in-text link styling on WRAPPED lines (#413): the wrapped-line case `link_check.py` deliberately
avoids (it fixes `COLS=120` so URLs don't wrap). Runs at a narrow `COLS=50` where the seeded Description
line (`Parent ticket: https://app.clickup.com/t/86a1b2c3d …`) word-wraps so the task URL lands on a
continuation row, then asserts the underline covers **exactly** the URL cells and never the trailing
prose. Terminal.Gui 2.4.10's word wrap keeps a wrapped row's graphemes but rebuilds its attributes from
source index 0, so the pre-#413 underline was painted `len("Parent ticket: ")` columns too far right;
the app now recomputes link cells per rendered row from the row's own graphemes. This is the **only**
check that exercises the true wrapped-render draw path (the unit tests cover the pure helper on
unwrapped lines), so run it whenever the detail-pane link styling or wrapping changes:

```bash
E2E_TASKS=20 timeout 90 python3 -u tests/ClickUpTodo.Tui.E2E/link_wrap_check.py $DLL
```

Self-contained (fixed `COLS=50`). Expected: `ok — wrapped task URL underlined exactly (no shift into
trailing prose), COLS=50`. Fails on the pre-#413 code (underline shifted right off the URL).

**`tab_boundary_check.py`** — Task Detail tab-boundary crash guard: Terminal.Gui 2.4.10's stock `Tabs` control crashes
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

**`link_click_check.py`** — link click activation (#318): drives real SGR mouse clicks at the same two seeded links and
checks where each gesture goes: `Ctrl`+click a **task** link → the browser; plain click a **web** link
→ the browser; plain click the **task** link → that task's Task Detail, stacked in-app (proven by the
extra `Esc` it then takes to reach the list); a click while the comment composer is open → nothing.
Ordinary clicks (prose, empty space right of a line, below the body) stay inert:

```bash
timeout 120 python3 -u tests/ClickUpTodo.Tui.E2E/link_click_check.py $DLL
```

Self-contained (sets its own `E2E_BROWSER_LOG`, so browser launches are asserted from the recorder
file rather than guessed from the screen). Expected: `ok — Ctrl+click → browser, web click → browser,
task click → stacked detail, click under an open composer → inert`. Ctrl is the `+16` modifier bit on
the SGR button code (`ESC[<16;x;yM`).

**`link_tab_check.py`** — link keyboard focus traversal + activation (#319): the keyboard counterpart of `link_click_check.py`. Drives
`Tab`/`Shift+Tab` (Tab = `0x09`, Shift+Tab = `ESC[Z`) to move a focus highlight across the same two
seeded links and `Enter` to activate the focused one, asserting `Enter` reaches the same destinations a
click does: an unfocused `Enter` is inert; `Tab` highlights the Description **task** link (a pyte
cell-attribute change) and `Enter` opens its Task Detail stacked in-app (proven by the extra `Esc` to
reach the list); `Shift+Tab` highlights the Comments **web** link and `Enter` opens the browser:

```bash
timeout 120 python3 -u tests/ClickUpTodo.Tui.E2E/link_tab_check.py $DLL
```

Self-contained (sets its own `E2E_BROWSER_LOG`). Expected: `ok — unfocused Enter inert; Tab highlights +
Enter opens the task link's detail (stacked); Shift+Tab highlights + Enter opens the web link in the
browser`.

**`osc8_link_check.py`** — OSC-8 terminal hyperlinks (#380): opens Task Detail and asserts that the two links #317 seeds
(the task link on the Description, the web link on the Comments tab) are each wrapped in an OSC-8
hyperlink escape (`ESC ] 8 ; ; <url> ST … ESC ] 8 ; ; ST`) targeting their own URL. This is the **one
check that asserts on raw bytes** (see the pitfall below): OSC-8 is a hyperlink escape a VT emulator
consumes, so it never appears on the pyte screen — pyte is driven only to boot/navigate:

```bash
timeout 90 python3 -u tests/ClickUpTodo.Tui.E2E/osc8_link_check.py $DLL
```

Self-contained (fixed COLS=120 so the seeded URLs don't wrap — a wrapped link is out of #380's scope,
tracked with the wrapped-line rendering work, #413). Expected: `ok — task link (Description) and web link
(Comments) each wrapped in a bounded OSC-8 hyperlink`. Invisible to the text-only `detail_check.py` A/B
and to `link_check.py`'s pyte styling assertions, which both stay green.

**`markdown_osc8_check.py`** — OSC-8 hyperlinks for markdown `[text](url)` links (#430): the case `osc8_link_check.py` defers. With
`E2E_MD_LINK=1` the fake backend appends `See [the runbook](https://example.com/runbook-42) for steps`
to the Description; this opens Task Detail and asserts, again on **raw bytes**, that the markdown link's
*visible text* (`the runbook`) is wrapped in a bounded OSC-8 escape whose target is the **resolved** URL
(`https://example.com/runbook-42`), not the visible prose — proving the target came from the markdown
markup, not a reconstruction of the drawn cells:

```bash
timeout 90 python3 -u tests/ClickUpTodo.Tui.E2E/markdown_osc8_check.py $DLL
```

Self-contained (sets its own `E2E_MD_LINK=1`; fixed COLS=120 so the markup doesn't wrap — a markdown
link split across two rendered rows is out of scope, #443). Expected: `ok — markdown link visible text
'the runbook' wrapped in a bounded OSC-8 hyperlink to its resolved target https://example.com/runbook-42`.
Because the seed is env-gated, every other check (which never sets `E2E_MD_LINK`) sees the original body
byte-for-byte — `osc8_link_check.py`, `link_check.py`, and `detail_check.py` A/B all stay green.

**`thread_check.py`** — threaded comments render nested (#329): opens Task Detail, cycles to the Comments tab, and
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

**`single_task_tree_check.py`** — Task Tree tab in single-task launch mode (#374): boots `SingleTaskApp` straight into `t0`
(`E2E_SINGLE_TASK=t0` + `E2E_TREE=1`, i.e. `clickup-todo --task t0`), cycles to the Task Tree tab, and
asserts the wiring single-task mode gained: the tab is present and renders the ancestry + task +
descendants; F6 cycles the badge display through all three modes (dashboard parity, #415); activating a
non-current row (Enter after a click-select, and a double-click) **stacks** that task's detail so a
single Esc walks back to the launch task; and Esc at the launch-task root hands off to the #299 exit
confirmation (no main list to fall back to). Self-contained (sets its own env):

```bash
timeout 120 python3 -u tests/ClickUpTodo.Tui.E2E/single_task_tree_check.py $DLL
```

Expected: `ok`. Note: the Enter leg selects its row with a click rather than ↑/↓, and selects it
deterministically before Enter drives the real keyboard activation path. (Bare ↑/↓ row selection *is*
now exercisable under the PTY once #452 landed — `detail_arrow_check.py` asserts it directly — but this check keeps the
click-select so its Enter leg stays pinned to one specific row regardless of where selection starts.)

**`detail_arrow_check.py`** — bare ↑/↓ scroll / row-move in Task Detail (#452): bare arrows used to be inert on every Task
Detail tab: the read-only text panes moved an invisible caret instead of scrolling, and the Task Tree
`ListView`'s `Command.Down` bubbled up to `NavSafeTabs`' inert crash-guard, cancelling its own
`MoveDown`. The app now claims bare ↑/↓ in `TaskDetailScreen.OnKey`. At a short terminal (so the Stream
body overflows) this asserts a bare ↑ scrolls the Stream pane up one line and a following ↓ scrolls it
back (viewport scroll, not caret), then on the Task Tree tab a bare ↓ moves the highlighted row down one
and ↑ moves it back. Requires `E2E_TREE=1`. Fails on the pre-#452 code:

```bash
E2E_TASKS=6 E2E_TREE=1 timeout 90 python3 -u tests/ClickUpTodo.Tui.E2E/detail_arrow_check.py $DLL
```

Self-contained (sets `E2E_TREE=1` itself). Expected: `ok — bare ↑/↓ scroll the Stream pane one line
(both directions) and move the Task Tree selection one row`. Pairs with `tab_boundary_check.py`: together they pin that a bare arrow moves content *within* a tab but is still a no-op at a content
boundary — never a tab switch or the `NavSafeTabs` crash.

**`checklist_check.py`** — Checklists tab in Task Detail (C, #456): opens Task Detail and cycles to the **Checklists** tab
(index 4, inserted after Other and before the Task Tree tab). With `E2E_CHECKLISTS=1` the fake backend
serves the opened task with a seeded `checklists` array (two groups, a nested item, mixed resolved
state, one assigned item); the check asserts both group headers render with their `resolved/total`
progress, every item carries a `[x]`/`[ ]` glyph, the nested item's checkbox sits further right than its
parent's (indentation), the assignee suffix shows on the one assigned item, the tab title reads
`Checklists (2/5)`, and bare ↑/↓ move the selection within the tab without ever switching away from it
(the NavSafe boundary contract, pairing with `tab_boundary_check.py`). A second leg (no `E2E_CHECKLISTS`) asserts a
checklist-free task shows the single empty-state row and a bare `Checklists` title:

```bash
E2E_TASKS=6 timeout 120 python3 -u tests/ClickUpTodo.Tui.E2E/checklist_check.py $DLL
```

Self-contained (drives both legs, sets its own env). Expected: two `ok —` lines (populated + empty).
Adding this tab shifted the Task Tree tab from index 4 to 5, so the fixed tab-cycle counts in
`tree_tab_check.py`, `detail_arrow_check.py` and `single_task_tree_check.py` were bumped 4→5 (the A/B
`detail_check.py` stays byte-identical — the extra tab renders in both legs).

**`mention_check.py`** — @-mention authoring in the comment composer (#325): boots the dashboard `TodoApp`, opens Task
Detail, and drives the `Ctrl+N` composer twice: a **plain** comment (no `@`) and a **mention** comment
(type `hi `, press `@` to open the mention picker, type `Ada`, `Enter` to insert the `@Ada Lovelace`
token, then Tab→Post→Enter). Asserts the plain post goes through the plain-text path (`comment_text`, no
tag) and the mention post through the structured path (a `{"type":"tag","user":{"id":101}}` block, Ada
Lovelace being seeded member 101), reading the actual POST bodies from the harness's `E2E_COMMENT_LOG`
recorder — a file fact, not a screen guess — plus the on-screen `@Ada Lovelace` token in the composer
and the posted comment in the pane. The member pool is the assignee top-up (`GET /team`) the #325 wiring
projects into `WorkspaceMember`s, so no new fake endpoint is needed:

```bash
E2E_TASKS=20 timeout 120 python3 -u tests/ClickUpTodo.Tui.E2E/mention_check.py $DLL
```

Self-contained (sets its own `E2E_COMMENT_LOG`). Expected: `ok — plain comment → plain-text path …;
@-mention → structured tag block for member 101 …`. Invisible to the text-only `detail_check.py` A/B
(the composer/overlay are hidden until `Ctrl+N`), which stays identical.

**`single_task_title_check.py` + `single_task_title_refresh_check.py`** — single-task terminal title on launch + refresh (#418/#425): `SingleTaskApp` titles its
top-level `Window.Title` with the launched task (custom id preferred, `{id}: {name}` ≤40 chars), which
Terminal.Gui emits to the host terminal as an OSC title escape (captured by pyte's `screen.title`), so
several `--task` tabs stay distinguishable on the tab strip. Two checks, each self-contained:

```bash
# #418 — title is set at launch, truncated to 40 chars
E2E_SINGLE_TASK=t5 timeout 60 python3 -u tests/ClickUpTodo.Tui.E2E/single_task_title_check.py $DLL
# #425 — title UPDATES on refresh: E2E_TITLE_REFRESH renames the launch task after the boot read,
#        Ctrl+R re-fetches, and screen.title must change to the renamed title
timeout 60 python3 -u tests/ClickUpTodo.Tui.E2E/single_task_title_refresh_check.py $DLL
```

Expected: `single_task_title_check.py` → `ok (title='t5: My Account - Address display  (EA-72')`;
`single_task_title_refresh_check.py` → `ok — title updated on refresh (… -> 't5: Renamed on refresh')`.
The `E2E_TITLE_REFRESH=1` gate (set by the refresh check itself) is opt-in, so the launch check and
every other scenario see the fixed launch-task name. `TerminalTitleTests` pins the pure
`ForTask`/`Retitle` formatting + decision in CI; these checks are the proof the title reaches the
terminal at launch and again on refresh.

## Pitfalls (violating these produced false "the TUI can't be tested" conclusions)

- **Answer the terminal's queries or nothing ever renders.** Terminal.Gui's ANSI driver
  asks the terminal for size (`ESC[18t`) and cursor position (`ESC[6n`); the scripts'
  `answer_queries()` replies on every read. A dumb pipe that never answers = permanently
  blank app.
- **Assert on the pyte screen, never on raw output bytes — with two exceptions.** With
  diffed flushing, text reaches the terminal as fragments interleaved with cursor moves —
  `b"Task 1"` may never appear contiguously in the byte stream even though the screen is
  perfect. Raw bytes are valid only for (a) *volume* metrics and (b) *escape*-sequence
  checks for things a VT emulator consumes rather than renders, so they never reach the
  pyte screen at all — e.g. OSC-8 hyperlinks (`osc8_link_check.py`). Even then, accumulate the whole
  stream and search it; an escape wrapping one repainted run is contiguous within that run.
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
