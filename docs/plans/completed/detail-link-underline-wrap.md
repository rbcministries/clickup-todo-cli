# Plan — Fix link underline columns on wrapped detail-pane lines (issue #413)

A correctness bug in the task-detail panes: on any **word-wrapped** line, the
in-text link underline (#317) is painted in the wrong columns — shifted right by
the length of the prose that preceded the URL on the *unwrapped source* line.

## Root cause (from the issue, confirmed in code)

`DetailPaneView.BuildCells` tags the cells covered by each detected link with
`TaskLinkMarker` / `WebLinkMarker` (per-cell attributes on the **source** line),
and `DetailPaneView.OnDrawReadOnlyColor` underlines whatever cell carries that
tag. Terminal.Gui 2.4.10's word wrap, when a source line spans multiple rendered
rows, rebuilds each wrapped row's **graphemes** from the wrap offset but its
per-cell **attributes** from index 0 of the source line. So on every row *after
the first*, the link tags sit on the wrong cells and the underline is drawn at
`offset = len(prose-before-URL-on-source-line)` too far right (issue's `COLS=56`
capture: 15 columns late for `"Parent ticket: "`).

Separator rows are unaffected: a separator source line is tagged **uniformly**
(`SeparatorMarker` on every cell), so the wrap misalignment maps one uniform
attribute onto another — the tag stays correct on every wrapped row.

## Fix

Stop trusting the wrapped cell's *link* attribute at draw time. Instead
recompute link cells **per rendered row from that row's own graphemes**:

- Add a pure helper `DetailPaneView.ClassifyRowLinkCells(IReadOnlyList<Cell>)`
  that reconstructs the row's text from `Cell.Grapheme`, re-runs
  `TaskLinkExtractor.Extract` on it, and returns one `DetailCellStyle` per cell
  (`TaskLink` / `WebLink` / `Normal`). Because word wrap breaks on whitespace and
  a URL contains none, a URL that fits the pane width lands wholly on one wrapped
  row, so per-row re-extraction yields row-local offsets that are correct with no
  wrap-offset arithmetic. Grapheme-length (not cell-index) accounting keeps the
  offset mapping correct when wide graphemes precede a URL.
- `OnDrawReadOnlyColor` keeps its **separator** branch (tag-driven; correct under
  wrap as above) and switches its **link** branch to the recomputed styles,
  cached per row (keyed on the row `List<Cell>` reference) so the re-extraction is
  once-per-row, not once-per-cell. A cheap `IndexOf("http")` bail-out skips the
  regex on the overwhelming majority of rows that carry no URL.

`BuildCells` link tagging and `ClassifyCell` are **unchanged** — they remain the
accurate source-line model the unit tests and the (separate) click path consume;
only the *draw-time* consumption of the link tag changes.

### Why not the alternatives (from the issue)

- *Bump Terminal.Gui* — the pin (2.4.10) is load-bearing across the driver
  hardening; a version bump is out of scope for a targeted rendering fix.
- *Pre-wrap + `WordWrap=false`* — a much larger change (own reflow-on-resize),
  and it would move the wrap responsibility out of Terminal.Gui.

## Acceptance criteria (from the issue)

- On a line long enough to wrap, the underline covers exactly the URL's cells, in
  every pane. ✔ recomputed per row from the row's graphemes.
- `link_check.py` (or a new check) asserts underline positions at a **narrow**
  `COLS` where the seeded URLs wrap — the case currently unguarded. ✔ new
  `link_wrap_check.py`.

## Tests

- **Unit** (`DetailPaneViewTests`): `ClassifyRowLinkCells` returns the URL cells
  as `TaskLink` / `WebLink` and everything else `Normal`, on a row that starts
  with prose before the URL (the wrapped-row shape), on a wrapped continuation
  row whose text begins mid-sentence, for both task and web links, and returns
  all-`Normal` for a link-free row and a bare separator fragment.
- **E2E** (`link_wrap_check.py`): run at a narrow `COLS` so the seeded
  Description task URL wraps onto a continuation row, and assert (a) the underline
  covers exactly the URL cells and (b) no non-URL cell on that row is underlined.

## Out of scope / deferred

- A URL **longer than the pane width** is hard-wrapped mid-URL by Terminal.Gui;
  per-row re-extraction then styles only the row-fragments that still parse as a
  URL. This is a pre-existing rendering limit, not introduced here; tracked
  separately if it ever matters in practice.
- Markdown `[text](url)` links whose `[text]` and `(url)` land on *different*
  wrapped rows: the visible text is left unstyled on the split rows (better than
  today's wrong-column underline). Same follow-up bucket.
</content>
</invoke>
