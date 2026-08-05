# Task Detail: compose bare ↑/↓ with PgUp/PgDn on one scroll state (#468)

Follow-up to #452 / PR #467. A low-severity discontinuity, not a break.

## What the issue expected

#452 made bare `↑`/`↓` scroll a read-only Task Detail text pane one line by moving the
pane's **viewport** directly (`MoveActiveTab` → `pane.Viewport = new Rectangle(...)`),
leaving the pane's invisible caret where it was. The issue anticipated that
`PgUp`/`PgDn` were still the stock `TextView` caret commands (`Command.PageUp`/`PageDown`
"move the caret a page, then `EnsureCaretVisible` repositions the viewport"), so the two
gestures would scroll via **different state**: a `PgUp` after a few `↑` would page from the
now-stale, off-screen caret and the view would jump to reveal it before paging.

## Empirical finding — the seam does not reproduce on Terminal.Gui 2.4.10

Before changing anything I instrumented the real app under the PTY harness and logged the
front-most pane's `viewport.Y` before/after each key, replicating the issue's exact repro
(auto-scroll to the bottom → bare `↑` ×N → `PgUp`/`PgDn`) at several decoupling depths
(N = 2, 4, 8, 80):

| gesture | viewport.Y before → after |
| --- | --- |
| `PgUp` (caret pinned at the last line, far below) | 206→189, 204→187, 200→183, 128→111 |
| `PgDn` | back up by the same one-page delta each time |

Stock `Command.PageUp`/`PageDown` on the read-only, word-wrapped pane **already page the
viewport** by one page (`height − 1` rows) and **never jump to the caret** — at any depth.
So in this TG version the premise "`PgUp`/`PgDn` move the caret" is false, and bare `↑`/`↓`
and `PgUp`/`PgDn` already compose without fighting. (My first attempt at an E2E "failed
pre-fix" only because it wrongly assumed 80 `↑` presses reached row 0; against a 226-line /
height-18 pane the viewport was at row 128 — the assertion was measuring my own bug, not a
seam.)

## Decision — own the composition explicitly (behaviour-preserving today)

Rather than rely on a Terminal.Gui internal that the **cross-platform epic (#312)** can't
assume holds across TG versions, terminals, and drivers, this change makes `PgUp`/`PgDn`
scroll through the **same explicit viewport model** as `↑`/`↓` (issue **option 1**). It is
behaviour-preserving on 2.4.10 (same one-page delta, same viewport write), and its value is:

- **Symmetry with #452** — the whole scroll vocabulary of the read-only panes (`↑`/`↓` *and*
  `PgUp`/`PgDn`, reading path *and* the composer's "scroll underlying") lives in one owned
  viewport model, not split between our code and TG's stock paging.
- **Driver-independence** — if a future TG upgrade or a different terminal driver makes
  `Command.PageUp/PageDown` caret-based (the exact behaviour #468 feared), the composition
  stays correct instead of silently regressing on some platforms.
- **Unit-tested page arithmetic** and a first-ever E2E that exercises `PgUp`/`PgDn`.

The panes are `WordWrap = true` (`DetailPaneView`), so `viewport.Y`/`pane.Lines` are
**wrapped-row** counts while the caret is a **logical** line index — the two live in
different coordinate spaces. Owning `PgUp`/`PgDn` in wrapped-viewport coordinates (like
`↑`/`↓`) sidesteps that entirely; the issue's "sync the caret" option 3 would instead need a
fragile wrapped↔logical mapping through the internal `WordWrapManager`.

## Approach

Mirror the codebase's pure-glue split: the arithmetic in `DetailScrollModel` (unit-tested),
only the Terminal.Gui wiring in the screen. Reuse the existing edge-saturating `NextTop`
clamp; the only new arithmetic is the page size.

### 1. Pure model — `DetailScrollModel`

- `PageDelta(viewportHeight)` → `max(1, viewportHeight − 1)`: one viewport page with a
  single line of overlap (the terminal-pager convention). Degenerate viewport (height ≤ 1)
  still advances one row. Because both gestures clamp the same `viewport.Y` via `NextTop`,
  `↑` then `PgUp` compose **additively** (a line delta then a page delta equals their sum).

### 2. Glue — `TaskDetailScreen`

- `PageActiveTextPane(int direction)` (−1 up / +1 down): a viewport write via
  `NextTop(vp.Y, vp.Height, pane.Lines, direction * PageDelta(vp.Height))` — the page
  counterpart of `MoveActiveTab`'s one-line branch. Returns `false` when the front-most tab
  is not a `TextView` (the Task Tree `ListView` keeps its stock page-selection).
- New `OnKey` block after the bare `↑`/`↓` block, before the `Ctrl`-chords: a **bare**
  `PageUp`/`PageDown` (no `Ctrl`/`Shift`/`Alt`) while the Dispatch prompt is closed, consumed
  only when `PageActiveTextPane` handled it. `Ctrl+PgUp`/`Ctrl+PgDn` (Stream-sort) are
  excluded by `!key.IsCtrl` and still reach their handlers.
- `ScrollActiveTab(Command)` (the composer's "scroll underlying") routes a `PageUp`/`PageDown`
  on a `TextView` through `PageActiveTextPane` too, so that path shares the same viewport
  model.

No change to `NavSafeTabs`, `CycleTab`, focus handling, the single sectioned `ListView`
input model, or the Task Tree / Other tab paging behaviour.

## Acceptance criteria (from #468) — status

- *"After any mix of `↑`/`↓` and `PgUp`/`PgDn`, each gesture continues from the currently-
  visible position (no jump to a stale caret line)."* — Holds; and after this change it is an
  owned invariant, not a Terminal.Gui default.
- *"`↑`/`↓` still scroll one line and remain no-ops at the content boundary"* — unchanged
  (`tab_boundary_check.py` stays green).
- *"A `tui-validate` check pins the composed behaviour (extend `detail_arrow_check.py`)."* —
  done (below). Note it is a **behavioural pin**, not a pre/post-fix discriminator, because
  stock 2.4.10 already composes.

## Tests

- **Unit** (`DetailScrollModelTests`): `PageDelta` (typical, height 1, height 0/negative
  degenerate); page-scroll composition via `NextTop` — `↑` then `PgUp` equals the summed
  delta; page clamps at both edges; a full-content-fits pane pages to a no-op.
- **E2E** (`detail_arrow_check.py`, extended; `E2E_LONG_STREAM` seeds a tall Stream at
  `ROWS=32`): with the viewport scrolled well up and the caret pinned far below, `PgUp` pages
  the viewport up and `PgDn` pages it back down (numbered filler lines are a monotonic
  position proxy) — pinning that `PgUp`/`PgDn` scroll the shared viewport and compose with
  `↑`/`↓`.
