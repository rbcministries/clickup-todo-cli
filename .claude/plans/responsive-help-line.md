# H2 — Responsive help line (#104)

Part of the #102 detail-view epic. Builds directly on #103 (the shared contextual
footer: `HelpLine` / `HelpItem` / `HelpItemSets`, `Screen.HelpItems`, and
`TodoApp.UpdateHelpLine` rendering onto the single `_helpLabel`).

## Goal

When the terminal isn't wide enough to show all of the active context's shortcuts on one
line, show as many as fit and make the **last** displayed item **"F1 Help + Shortcuts"**,
which opens the full list via the F1 `HelpScreen` (reachable from every screen since #103).
When everything fits, show all items unchanged.

## Acceptance (from the issue)

- (items, width) → visible subset + whether the "F1 Help + Shortcuts" trailer is appended,
  across widths from very narrow (only F1 fits) to wide (all fit).
- Width math uses a **column-width** measure (grapheme/rune aware), **not** `string.Length`,
  so the emoji/wide glyphs already in the footers (`🌐 📌 ↻ ⚙`, arrows) count as their true
  column width. Account for the `·` separator and the leading/trailing padding.
- Recompute on resize so the line re-fills when the window widens/narrows.
- Item order = priority: keep the highest-value (leading) shortcuts first when truncating.

## Design

### Pure helper (`HelpLine`, stays Terminal.Gui-free)

- `HelpLine.HelpFallback` = `new HelpItem("F1", "Help + Shortcuts")` — the trailing item.
- `HelpLine.Fit(IReadOnlyList<HelpItem> items, int width, Func<string,int> measure)`:
  - If `items` empty → return as-is.
  - If `measure(Format(items)) <= width` → return `items` unchanged (everything fits; the
    set already lists its own `F1 help` item, so no extra fallback is added).
  - Otherwise (truncating): greedily keep the longest **leading** prefix that fits together
    with a **reserved** trailing `HelpFallback`, then append the fallback. Any existing
    `F1`-keyed item is dropped from the candidates (the fallback subsumes it) so F1 never
    renders twice. At a width too narrow for even the fallback, returns `[HelpFallback]`
    (it still renders, clipped by the host) — the "only F1 fits" case.
  - The measure is injected so `HelpLine` stays free of Terminal.Gui; callers pass
    `s => s.GetColumns()` (Terminal.Gui's grapheme-aware `StringExtensions.GetColumns`).
    `Format` already inserts the `·` separators, so measuring `Format(candidate)` accounts
    for separators + inter-token spaces.

### Wiring (`TodoApp`)

- `UpdateHelpLine` fits the active context's items to the footer's current content width
  (`_helpLabel.Frame.Width`) using `GetColumns`, and assigns only when the text actually
  changes (so re-layout can't loop). Before the first layout (width ≤ 0) it renders the full
  set; the first layout pass then re-fits.
- Subscribe to `_window.SubViewsLaidOut` (Terminal.Gui 2.4 has no static
  `Application.SizeChanging`) → `UpdateHelpLine`, so a terminal resize re-fills the line.
  The change-guarded assignment prevents the layout→update→layout loop.

## Tests (`HelpLineTests`)

- Fit returns the set unchanged when it fits (wide width) and for the empty set.
- Fit truncates: prefix + `HelpFallback` last; the rendered line's column width ≤ width;
  more items appear as width grows (monotonic).
- Very narrow width → only `HelpFallback`.
- Column-width (not char-count) lock-in: a set with a wide emoji fits/does-not-fit at a width
  where char-count would decide the opposite (measured with the real `GetColumns`).
- Fit never yields two `F1`-keyed items (no duplicate F1 with the fallback).

## Out of scope / not changed

- No new focusable pane; the single sectioned `ListView` model (#3) is untouched.
- No keybinding changes (F1 already opens Help from every screen since #103).
- TUI/resize behaviour verified by build + reasoning per the repo's TUI rule; `tui-validate`
  can assert the rendered line at a couple of widths after `dotnet test` is green.
