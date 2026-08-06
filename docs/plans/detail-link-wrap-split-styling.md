# Plan — Style link tokens that word wrap splits across two rendered rows (#443)

Follow-up to **#413** (PR #440, the wrapped-line underline fix) and **#430** (PR #478,
markdown-link OSC-8), part of epic #313. Both shipped a **per-row re-extraction**
(`DetailPaneView.ClassifyRow`) that recomputes a rendered row's link cells from that
*row's own graphemes* — correct and offset-free **as long as the whole link token sits on
one rendered row**. Both deliberately left the same gap, and both point it here:

1. **A bare URL longer than the pane inner width.** Terminal.Gui 2.4.10 hard-wraps it
   mid-URL. Per-row re-extraction sees each fragment independently: the head fragment may
   still parse as a (shorter) URL and get underlined, the tail fragment doesn't parse and
   stays unstyled — a partially-underlined URL.
2. **A markdown `[text](url)` link whose visible text wraps across rows.** No single row
   holds the whole `[text](url)` pattern, so per-row re-extraction matches nothing on the
   continuation row(s) and the visible text is left unstyled there.

## Why the per-row technique can't close this

Per-row re-extraction is, by construction, blind across the wrap boundary — the split
fragments don't re-assemble into the link the extractor finds on the *unwrapped source*
line. Closing the gap needs the **display→source-line mapping**: which source line, and at
what character offset, each rendered row came from. With that, styling is driven from the
**source line's** `LinkSpan`s (which see the whole, unsplit link), and a link's cells are
styled contiguously no matter how many rows it wraps onto.

That mapping is owned by Terminal.Gui's `internal WordWrapManager` — the reason #413/#430
avoided it. But the issue itself sanctions the alternative: *"mapping by published buffer
position, à la #318's click hit-testing."* We take that path.

## Key realisation — reconcile the *published* wrap output, don't reimplement wrap

`TextView` already exposes its finished wrapped layout publicly via `GetAllLines()` (the
same rows the draw path paints; `LinkAt`/`EnsureFocusedLinkVisible` already read them). And
`SetBody` retains the source lines verbatim in `_lines`. Word wrap only ever **splits** a
source line into ≥1 consecutive display rows in order — it never merges or reorders — so
the display→source mapping is recoverable by **reconciling the two published sequences**:

- Walk the wrapped rows in order, tracking the current source-line index and a search
  cursor into it. Each wrapped row's reconstructed text is located in the current source
  line with `IndexOf(rowText, cursor)` (advancing the cursor); when it isn't found there
  the row belongs to a later source line, so advance. Soft-wrap dropping the break space is
  handled for free — the next row's text is simply found *after* the gap. A hard-wrapped
  URL drops no char, so its fragments are found contiguously.

This is **not** a reimplementation of the wrap algorithm (which the code rightly refuses to
do); it is a reconciliation against Terminal.Gui's own finished output. It needs no
internal API and — because a real `DetailPaneView` wraps headlessly (`SetBody` + toggling
`WordWrap`, exactly as the #318 click tests already drive it) — it is **fully
unit-testable against genuine `GetAllLines()` output**, not hand-built rows.

## Design

Two new pure statics on `DetailPaneView`, plus a draw-path rewire. The existing per-row
statics (`ClassifyRow` / `ClassifyRowLinkCells` / `RowLinkUrls` / `LinkUrlForCell`) are
**left unchanged** — they remain the correct isolated-row projection (and keep their #413 /
#430 tests, including the split-fragment safe-degradation pins) and become the **fallback**
when a row can't be reconciled, so the change is strictly better-or-equal to today.

1. **`BuildRowSourceMap(IReadOnlyList<string> sourceLines, string separator,
   IReadOnlyList<IReadOnlyList<Cell>> wrappedRows) → IReadOnlyList<RowSource>`** — the pure
   reconciliation above. `RowSource` is `(int SourceLineIndex, int StartOffset)`
   (`StartOffset` = the char offset within the source line where the row begins). Order of
   the result matches `wrappedRows`. Robust to empty/blank source lines and separators.

2. **`ClassifyRowFromSource(IReadOnlyList<Cell> row, string sourceLine, int startOffset) →
   (DetailCellStyle[] Styles, string?[] Urls)`** — classify each cell by the **source
   line's** `LinkSpan` that covers its source char offset (`startOffset` + the row's own
   accumulated grapheme length, the same wide-grapheme accounting as `ClassifyRow`). Because
   the spans come from the whole source line, a link split across rows is classified
   contiguously on every row it touches, and a markdown link's cells carry its **resolved**
   target — the #430 boundary closes here too, as a fall-out of the same mapping.

3. **Draw-path rewire.** A per-row cache keyed on the row `List<Cell>` reference (parallel
   to today's `_linkRow`/`_linkRowStyles`/`_linkRowUrls`): on a miss, look the row up in a
   reference-keyed **source map** built once from `GetAllLines()` (rebuilt when a row isn't
   found — i.e. the wrap changed). If the row reconciles, classify via
   `ClassifyRowFromSource`; if it doesn't (defensive), fall back to the per-row
   `ClassifyRow`. `OnDrawReadOnlyColor` and `OnDrawComplete` are otherwise unchanged.

## Acceptance criteria (from the issue)

- A bare URL wider than the pane is underlined across **both** rendered rows it wraps onto,
  exactly the URL cells. ✔ both fragments map to the one source span.
- A markdown `[text](url)` link whose visible text and URL wrap onto different rows still
  has its **visible text** underlined. ✔ visible-text cells map to the markdown span on
  every row; the `[`/`]`/`(url)` markup stays outside the span, so it is left unstyled.
- A `tui-validate` check at a narrow `COLS` asserts both (extending `link_wrap_check.py`). ✔

## Tests

- **Unit** (`DetailPaneViewTests`):
  - `BuildRowSourceMap`: driven against a **real headless pane's** `GetAllLines()` — a long
    URL hard-wrapped mid-token, a markdown link whose visible text wraps, a multi-paragraph
    body with separators and blank lines — asserting each wrapped row's `(SourceLineIndex,
    StartOffset)` reconstructs the row text as `sourceLine.Substring(StartOffset, len)`.
  - `ClassifyRowFromSource`: the split-URL continuation fragment is `WebLink`/`TaskLink`
    with the whole URL as its target; the split-markdown continuation fragment's
    visible-text cells are the link kind with the **resolved** target while the markup /
    `(url)` cells are `Normal`/`null`; a link-free row and a separator are all-`Normal`.
  - End-to-end through the pane: build at a narrow width, `GetAllLines()`, map, classify,
    and assert the split URL / split markdown are styled contiguously across rows — the
    exact gap #413/#430 left, now closed.
- **E2E** (`tui-validate`, `link_wrap_check.py` extended, behind a new `E2E_WRAP_SPLIT`
  seed gate so every other check stays byte-identical): at a narrow `COLS`, a seeded
  over-long URL wraps across two rows and **every** URL cell on **both** rows is underlined;
  a seeded markdown link's visible text wraps and its continuation-row visible-text cells
  are underlined. The existing default-body assertions (the `Parent ticket:` line) stay.

## Scope boundary

- **Keyboard-focused-link emphasis (#319) on a continuation row.** The focus cue is still
  tag-driven (`BuildCells` → `FocusedLinkMarker`), which word wrap misaligns on continuation
  rows exactly as it misaligned the underline pre-#413. Re-deriving it from the same source
  map is a natural follow-on but is a *different* cue (a fresh styling decision, not the
  underline this issue is about) and touches the focus branch, so it stays out of scope here
  and is noted in the PR as remaining. The underline/OSC-8 styling this issue owns is fixed.

## Hard rules honoured

- No `Generated/` hand edits; no spec change / no Kiota regen — pure UI helper + a
  data-only draw-path rewire.
- Single sectioned `ListView` input model untouched — no new focusable pane / keybinding /
  driver change. The OSC-8 escape is still emitted by the stock ANSI output from
  `IDriver.CurrentUrl`, exactly as #380 established.

## Phases

1. **Core + unit tests** — `RowSource`, `BuildRowSourceMap`, `ClassifyRowFromSource`;
   unit tests against real headless-pane wrap output. Build/test/format green.
   Commit/push → open draft PR.
2. **Draw-path wiring + E2E** — reference-keyed source-map cache in the draw path with the
   per-row fallback; `E2E_WRAP_SPLIT` seed + `link_wrap_check.py` extension + `SKILL.md`
   note. `gh pr ready`.
