# Fix: status/priority badge colour overlay offset from its text (#63)

## Problem

`StatusBadgeListSource.OverlayBadge` re-draws a row's `[status]`/`[priority]` badge in its ClickUp
colour on top of the text the stock `ListWrapper<string>` already rendered. To position the badge it
recomputes the badge's display column by walking the row text **rune by rune** and summing
`Math.Max(1, rune.GetColumns())`.

The stock renderer positions text differently. Terminal.Gui's `AddStr` advances by **grapheme
cluster**, and its width function `StringExtensions.GetColumns`:

- iterates **grapheme clusters** (`GraphemeHelper.GetGraphemes`, UAX #29 via `StringInfo`),
- sums each cluster's rune widths, treating **negative** (control) widths as **0**,
- **caps each cluster at 2 columns**.

So for a task **name** containing a combining mark, a ZWJ emoji sequence, or a wide/ambiguous rune,
the overlay's per-rune `Math.Max(1, …)` sum diverges from the base renderer's grapheme width, and the
coloured cells land a few columns off the bracketed text — the "`[to [to do]`" doubling in #63. Pure
ASCII names (the common case) are unaffected because there one rune == one grapheme == 1 column, so
both formulas agree.

`PaintHeaderBar` carries the identical per-rune width loop, so it has the same latent divergence.

## Fix

Position overlays with the **same grapheme-aware width the base renderer uses**, so text and colour
can never disagree:

1. Add a pure, Terminal.Gui-free helper `StatusBadgeListSource.LayOutGraphemes(string text)` that
   yields each grapheme with its start **display column** and **char (UTF-16) index**, accumulating
   width via Terminal.Gui's `StringExtensions.GetColumns` (per grapheme) — identical semantics to the
   base renderer (`text[..charIndex].GetColumns()` for every grapheme start). This is the single
   source of truth for column math; the two rune-walking loops are removed.
2. Rewrite `OverlayBadge` to iterate `LayOutGraphemes`, draw each grapheme in the badge's char span
   with `AddStr(grapheme)` (whole clusters, so ZWJ/emoji aren't split), clipped to the viewport by
   grapheme column width — mirroring the old `x >= 0 && x + width <= width` guard.
3. Rewrite `PaintHeaderBar` to reuse `LayOutGraphemes` for the text, then pad to `width`.

## Tests (`StatusBadgeListSourceTests`)

`LayOutGraphemes` is pure, so its column math is unit-testable even though the draw path isn't.

- **Alignment invariant:** for ASCII, CJK (wide), combining-mark, and ZWJ-emoji strings, every laid-out
  grapheme's `Column == text[..CharIndex].GetColumns()` — i.e. it matches the base renderer exactly.
- **Regression / bug capture:** for a name with a combining mark and for a ZWJ emoji, the badge's
  computed start column equals `text[..StatusStart].GetColumns()` **and differs from** the old
  `Σ Math.Max(1, rune.GetColumns())` formula (proving the offset existed and is fixed).
- **No-op for ASCII:** for a plain ASCII row the new start column equals both `GetColumns` and the old
  formula (the common path is unchanged).
- Drive the badge span from `TaskRowFormatter.Format` so the test uses the real `StatusStart`.

## Verification

- `dotnet build -c Release` (0/0), `dotnet test -c Release` (green), `dotnet format`.
- TUI itself isn't unit-testable in CI (repo rule). No new focusable pane; the change is width math
  inside the existing single sectioned `ListView`. Manual before/after: a task named with a combining
  accent or emoji shows the `[status]`/`[priority]` colour flush with the brackets instead of shifted.
