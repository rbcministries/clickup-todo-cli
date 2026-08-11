# Plan — keyboard-focused-link emphasis (#319) on a word-wrapped continuation row (#527)

Follow-up deferred from **#443** (PR #526), part of epic #313. #443 closed the wrap-split
gap for the link **underline / OSC-8** styling by recovering a display→source-line mapping
(`DetailPaneView.BuildRowSourceMap` / `RowSource`) and styling each rendered row from its
**source line's** `LinkSpan`s (`ClassifyRowFromSource`) rather than the per-cell tags word
wrap misaligns. It deliberately left one adjacent residual out of scope: the
**keyboard-focused-link emphasis** (#319 — the `Focus`-role reverse-video cue `Tab` /
`Shift+Tab` steps across links) is still **tag-driven**.

## The gap

`BuildCells` tags the focused link's cells with `FocusedLinkMarker` on the **source** line,
and `OnDrawReadOnlyColor` reads that tag (`attr.Equals(FocusedLinkMarker)`) to paint the
focus cue. Terminal.Gui 2.4.10's word wrap rebuilds a wrapped row's attributes from source
index 0, so on a **continuation row** the focus tag lands on the wrong cells — exactly the
misalignment #413 worked around for the underline, but for the focus cue. A focused link
that fits on one row (the common case) is unaffected; only a focused link that itself wraps
shows the cue on the wrong columns of its continuation row(s).

## The fix — drive the focus cue from the #443 source map, not the tag

The link **kind** cue (task/web underline) is already recomputed at draw time from the
source-line spans via `LinkStyleAt` → `EnsureRowLinkCache` → `ClassifyRowFromSource`, so it
is offset-correct on every wrapped row. This change extends that same path to the focus cue:

1. **`ClassifyRowFromSource` gains an optional `LinkSpan? focusedSpan`.** When a link span on
   the source line value-equals `focusedSpan`, the covering cells are classified
   `FocusedLink` instead of their kind (`TaskLink`/`WebLink`); the resolved URL is still
   emitted (OSC-8 unchanged). Because the span comes from the whole source line, the focus
   cue is contiguous on **every** row the focused link wraps onto.
2. **`EnsureRowLinkCache` passes the focused span** for the row's source line — a new
   `FocusedSpanOnLine(sourceLineIndex)` helper returns `_paneLinks[_focusedLinkIndex].Span`
   when the focused link lives on that source line, else `null`. A focus change reloads
   (`RenderFocusedLink` → `Load(BuildCells(...))`), minting fresh row references, so the
   per-row style cache and the reference-keyed source map self-heal and re-read the new
   focus on the next draw.
3. **The draw path (`OnDrawReadOnlyColor`)** drops the `attr.Equals(FocusedLinkMarker)`
   branch and paints the focus cue from `LinkStyleAt` returning `FocusedLink` — the same
   theme `Focus` role + underline as before, now on the correct cells. The separator cue
   stays tag-driven (a separator row is tagged uniformly, so wrap leaves it correct).

`BuildCells` still applies the `FocusedLinkMarker` tag: `EnsureFocusedLinkVisible`
(scroll-into-view) and the test helpers locate the focused link's first row by that tag, and
that row is always correctly tagged (index 0). Scroll behaviour (#319) is untouched — this
is a **draw-time styling change only**, as the issue requires.

The non-reconciling fallback (`ClassifyRow`, per-row re-extraction) stays focus-unaware: a
row that fails to reconcile shows kind styling with no focus cue rather than a wrong-column
one. For the link *kind* this is strictly no-worse; for the *focus* cue it is a narrow
theoretical regression (pre-#527 an unwrapped focused link got its cue from the index-0
aligned tag without needing reconciliation, so a reconciliation miss on that same single row
would now drop the cue). Reconciliation succeeds for every real body — both #443's underline
and the passing E2E depend on it — so this defensive path is not hit in practice.

## Acceptance criteria (from the issue)

- With a focused link that word-wraps, its focus emphasis covers exactly the link's cells on
  **every** continuation row — driven from the source map, unit-tested against a real
  headless pane's `GetAllLines()` output and asserted in `tui-validate` at a narrow `COLS`.
- No regression to #443's underline/OSC-8 styling, #319's single-row focus traversal, or
  `detail_check.py` A/B (both renderers share `OnDrawReadOnlyColor`).

## Hard rules honoured

- No `Generated/` hand edits; no spec change / no Kiota regen — pure UI helper + a data-only
  draw-path rewire, reusing `RowSource` / `BuildRowSourceMap` (no second display→source map).
- Single sectioned `ListView` input model untouched — no new focusable pane / keybinding /
  driver change.

## Phases

1. **Core + unit tests** — `ClassifyRowFromSource(focusedSpan)`, `FocusedSpanOnLine`, draw
   rewire; unit tests against real headless-pane wrap output. Build/test/format green.
   Commit/push → open draft PR.
2. **E2E + ready** — extend `link_wrap_check.py` (it already owns the `E2E_WRAP_SPLIT`
   split-URL seed and the #443 machinery, so the focus leg reuses the same wrapped `ENDURL`
   tail): `Tab`-focus the split URL and assert the `Focus` cue lands on its continuation-row
   cells at narrow `COLS`, verified to fail on pre-#527 code. + `SKILL.md` note. `gh pr ready`.
