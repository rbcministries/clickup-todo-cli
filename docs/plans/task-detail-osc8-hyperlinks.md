# Task Detail links: emit OSC-8 terminal hyperlinks (#380)

Follow-up to **C** (#317, part of epic #313). #317 shipped the *visual* styling of
the links `TaskLinkExtractor` (#316) detects in the Description / Comments / Stream
panes (task links underlined; web links blue + underlined). This adds the **optional
terminal-native hyperlink layer** #317 deliberately left out: under the ANSI driver a
link cell also carries an OSC-8 URL, so a supporting terminal (Windows Terminal,
iTerm2, …) offers native open-on-click — independent of, and complementary to, the
app-level mouse activation in **D** (#318).

## What Terminal.Gui 2.4.10 already gives us

Verified against the pinned package before writing any code:

- The **stock ANSI output** already emits OSC-8 (`ESC ] 8 ; ; URL ST … ESC ] 8 ; ; ST`,
  ST = `ESC \`) around any run of cells that carry a URL. Confirmed by driving
  `OutputBase.ToAnsi` over a buffer with a cell URL set — nothing in the app or driver
  needed to change to make the escape appear.
- The draw-path seam is **`IDriver.CurrentUrl`** (a `string?` "gets or sets the URL
  associated with cells added via `AddRune`/`AddStr`") — the exact parallel of how
  `View.SetAttribute` drives `IDriver.CurrentAttribute`. Set it right before a link
  cell's rune is emitted and the subsequent `AddRune` tags the cell; the stock output
  wraps the run in OSC-8.
- The diff-flush ANSI output (`DiffFlushAnsi.cs`, the app default) already **tracks**
  the per-cell URL (`buffer.GetCellUrl`) in its row-changed comparison, so a link row
  flushes (and re-emits its OSC-8) whenever its URL changes and is skipped byte-for-byte
  when nothing changed — no diff-flush change needed.

`Cell` itself carries **no** URL in this version (only Attribute / Grapheme), and the
position-keyed `OutputBufferImpl.SetCellUrl(col,row,url)` is not on the `IOutputBuffer`
interface — so the URL cannot be tagged in `BuildCells` the way the color markers are.
It must be set in the **draw path**, keyed to the cell about to be drawn. That drives
the design below.

## Design

Purely additive to #317; no change to cell classification, colours, or `BuildCells`.

1. **Pure helper `DetailPaneView.LinkUrlForCell(line, idxCol)`** (unit-tested, no
   Terminal.Gui draw surface). Given a laid-out (display) line's cells and a cell index,
   returns the OSC-8 target for the link run covering that cell, or `null`:
   - `null` when the cell is not a task/web link cell (via the existing `ClassifyCell`).
   - Otherwise expands over the maximal contiguous run of same-kind link cells around
     `idxCol`, reconstructs the run's text from its graphemes, and returns it **only when
     that text is itself a navigable absolute `http(s)` URL** (a real host). Because a
     **bare** link's on-screen text *is* its URL, this yields the exact target for every
     bare task/web link — the case #317 renders and this issue validates.

2. **`OnDrawReadOnlyColor`** sets `Application.Driver.CurrentUrl = LinkUrlForCell(line,
   idxCol)` at the top of the override (for a non-link cell that is `null`, clearing any
   URL left by a preceding link cell), then runs the existing separator/link/normal
   colour logic unchanged. Setting it every cell means a link URL never bleeds into the
   following non-link cell.

3. **`OnDrawComplete`** resets `Application.Driver.CurrentUrl = null` after the pane
   finishes, so a link that is the pane's last drawn cell can't leak its URL onto a
   sibling view drawn later in the frame.

## Scope boundary (deferred, tracked)

- **Markdown `[text](url)` links** (where the visible text ≠ the target). The draw path
  sees only the displayed cells, so the true target isn't recoverable from them without
  the display→source-span mapping (Terminal.Gui's `WordWrapManager` is `internal`). The
  URL-revalidation guard makes these **safely skip** (a prose run like "click here" is
  not a URL → no OSC-8, never a *wrong* target). Full markdown-link OSC-8 needs the span
  threaded into the draw path — deferred to a new follow-up issue.
- **Word-wrapped links.** A URL split across display rows reconstructs per-row fragments;
  the tail fragment fails URL revalidation (skipped), while the head fragment can emit a
  truncated target. This is the same wrapped-line rendering limitation already tracked by
  **#413** (the link *underline* is mis-columned on wrapped lines); #380's validation, like
  #317's, pins a wide `COLS` so the seeded URLs don't wrap. Correct wrapped-link handling
  rides along with the #413 fix.

## Tests

- **Unit** (`DetailPaneViewTests`, through the real `Cell.ToCellList` model, no driver):
  `LinkUrlForCell` returns the full URL for any cell inside a bare task/web link run
  (first, middle, last cell), `null` for a non-link cell, `null` for a prose run that
  merely looks tagged, the correct per-run URL when two links share a line, and `null`
  at out-of-range indices.
- **E2E** (`tui-validate`, new `osc8_link_check.py`): boots the real app under the PTY,
  opens Task Detail, and asserts on the **raw byte stream** (OSC-8 is an escape sequence
  pyte strips — the harness's documented raw-byte *escape*-check exception) that the
  seeded task link (Description) and web link (Comments) are each wrapped in
  `ESC ] 8 ; ; <url> ST … ESC ] 8 ; ; ST`. Reuses the links #317's `link_check.py`
  already seeds (fixed wide `COLS` so they don't wrap) — no harness `Program.cs` change.
- **Regression**: `link_check.py` (styling), `detail_check.py` A/B (text-only dump stays
  identical — OSC-8 is invisible to it), and the output-volume baseline are unaffected.

## Verification notes (TUI)

The `CurrentUrl` wire-in is Terminal.Gui host code (not CI-unit-testable per `CLAUDE.md`);
the net-new decision logic (`LinkUrlForCell`) is fully unit-tested and the OSC-8 emission
is validated end-to-end by `osc8_link_check.py`. No new focusable pane, keybinding, list
source, or driver change — the single-`ListView` input model of #3/#38 is untouched.
