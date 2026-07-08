# Plan — Detail Other tab: reachability on a very short terminal (#81)

## Problem (from #81)

The detail view's **Other** tab (`TaskDetailScreen`) is a container:

- a **fixed-height, non-scrollable** coloured header (`DetailAttributesView`) at `Y = 0`,
  `Height = headerLines.Count` (up to ~7: List / [Lists] / Priority / Status / Created /
  Last activity / Due), and
- the scrollable, word-wrapped **custom-fields** `TextView` at `Y = headerLines.Count + 1`,
  `Height = Dim.Fill()`.

On a **very short** tab body (≲ 9 rows):

1. header rows past the viewport clip with **no way to scroll** to them, and
2. the custom-fields `TextView` collapses to zero/negative height, so **"Custom fields:" becomes
   unreachable** — a regression vs. the pre-#66 single scrollable `TextView`.

## Chosen approach — adaptive split + spillover (issue option 2, done fully)

Keep the coloured header a fixed non-scrollable view (avoids the hand-rolled vertical-scroll logic
PR #80 deliberately avoided — the #63 correctness risk), but make the split **adaptive**:

- Always reserve a **minimum scrollable body** so the custom-fields section stays reachable.
- When space is tight, **cap** the coloured header from the bottom and **spill the clipped trailing
  header lines into the top of the scrollable body as plain text**. Because the header is ordered
  `List, [Lists], Priority, Status, Created, Last activity, Due`, the lines that spill first are the
  **date lines** — which are *never* coloured — so spilling them into a plain `TextView` **loses no
  colour** while restoring full reachability (they scroll into view). Only on a pathologically tiny
  window would Priority/Status spill, degrading gracefully to uncoloured but still readable.

This resolves **both** stated regressions (header rows reachable via body scroll; custom-fields
always reachable) using only Terminal.Gui's built-in `TextView` scrolling — no hand-rolled scroll.

## Pieces

1. **`src/ClickUpTodo/Tui/DetailOtherTabLayout.cs`** (new, pure, unit-tested)
   `Compute(int headerLineCount, int availableHeight) -> Layout(HeaderHeight, BodyY, BodyHeight,
   SpilledHeaderLines)`.
   - `GapRows = 1`, `MinBodyRows = 3`, `MinHeaderRows = 1` (constants).
   - Fits (`headerLineCount + GapRows + MinBodyRows <= availableHeight`): full header, blank gap row,
     body fills the rest, no spill.
   - Constrained: `HeaderHeight = clamp(availableHeight - MinBodyRows, MinHeaderRows, headerLineCount)`;
     body starts immediately after the header (spilled lines carry the visual gap in text),
     `SpilledHeaderLines = headerLineCount - HeaderHeight`.
   - Degenerate (`availableHeight <= 0`, `headerLineCount == 0`) handled without negatives.

2. **`src/ClickUpTodo/Tui/DetailOtherTabView.cs`** (new; the CI-untestable Terminal.Gui glue)
   A `View` container that owns the `DetailAttributesView` header and the custom-fields `TextView`,
   and re-applies the layout in `OnSubViewLayout` (before subviews are laid out) from its
   `Viewport.Height`. Exposes the body `TextView` as the focusable scroll target. Rebuilds the body
   text with spilled header lines (plain `DetailLine.Text` + blank + custom fields) when
   `SpilledHeaderLines > 0`; only re-applies when the computed header height changes (so normal-size
   resizes don't churn scroll state).

3. **`TaskDetailScreen`** wiring: replace the inline `other` container + `attributesView` +
   `customFields` with `new DetailOtherTabView(headerLines, TaskDetailFormatter.CustomFieldsBody(task))`;
   use its body as the Other tab's `_scrollTargets` entry. No keybindings, no second focusable pane,
   single-`ListView` dashboard untouched.

## Tests

- `tests/ClickUpTodo.Tests/DetailOtherTabLayoutTests.cs` (new): fits case keeps full header + gap;
  constrained case reserves `MinBodyRows` and spills the exact overflow; header never below
  `MinHeaderRows` nor above `headerLineCount`; body never negative; monotonic across shrinking
  heights; degenerate inputs.
- Existing `TaskDetailFormatterTests` unchanged (formatter untouched).

## Verification

- `dotnet build -c Release` 0/0; `dotnet test -c Release` green.
- `dotnet format` clean.
- TUI glue verified by build + reasoning (per repo rule) and, if green, the `tui-validate` PTY
  harness to confirm the Other tab renders the coloured header + reachable custom fields at a normal
  size and that a short window keeps custom fields reachable. No new focusable pane; single sectioned
  `ListView` preserved.
