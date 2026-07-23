# Mouse/UX (B): click a parent's fold arrow to expand/collapse (#287)

Part of the Mouse interaction epic (#283). Builds directly on **A** (#286,
merged) — the shared `Services/RowHitTester.cs` row hit-test helper — and reuses
the same single-`MouseEvent`-handler-on-the-list pattern. No new views, no
second focusable pane (#3/#38).

## Goal

A single left-click on a parent task's fold arrow (`▶`/`▼`) toggles its
subtasks — the mouse equivalent of `→`/`←`. Keyboard fold (`→`/`←`,
`Ctrl+→`/`Ctrl+←`) is unchanged; native single-click selection and A's
double-click-to-open are untouched.

## Approach decision (recorded per the issue)

The issue offers **preferred** (only a click within the fold-arrow column
toggles) vs a **fallback** (single-click anywhere on a parent row toggles). We
implement the **preferred, arrow-column-scoped** approach:

- It cleanly resolves the double-click coexistence risk. Double-click-to-open
  (A) fires on the row body (the title), which is *not* the arrow gutter, so a
  toggle can never fire as the first half of a title double-click and thrash the
  fold.
- The acceptance criteria call for "fold-column resolution … unit-tested",
  which the arrow-column math satisfies directly.

Column hit-testing is made reliable across drivers/emoji widths by measuring
with the **same** grapheme/column-aware measure the renderer and the existing
help-bar hit-test use (`StringExtensions.GetColumns`, injected as a
`Func<string,int>` so the resolution stays pure and unit-testable — mirrors
`HelpLine.HitTest`).

Residual edge (accepted, noted in PR): a *double*-click landing on the arrow
itself both toggles (first click) and opens detail (double). Rare; the primary
gestures (single-click arrow = toggle, double-click title = open) are clean.

## Current state (verified)

- Rows live in row-parallel arrays on `TodoApp` (`_rows`, `_kinds`, `_display`,
  `_badges`, `_headerAttrs`, `_depths`, `_folds`). A row's fold state is
  `_folds[i]` (`FoldState.None`/`Collapsed`/`Expanded` — `SubtaskArranger.cs`);
  only `Collapsed`/`Expanded` rows carry a real `▶`/`▼` marker.
- The `▶ `/`▼ ` marker (2 chars) is emitted by `FoldMarker(fold, nest)` and
  inserted by `TaskRowFormatter.Format` right after the badges + indent, before
  the title. Its offset within the row text isn't currently exposed.
- Keyboard fold: `ExpandOrEnter()` (→) and `CollapseOrJumpToParent()` (←) mutate
  the ephemeral `_expanded` set and `Render(keepTaskId:)`. All gated on
  `_config.View.ShowSubtasks && ActiveScreen is null`.
- A (#286) added `RowHitTester` (pure) + `OnListMouse` on `_list`, handling only
  `LeftButtonDoubleClicked`. Terminal.Gui 2.4.10: `View.MouseEvent` args carry
  `Flags` (`MouseFlags`) and a viewport-relative `Position` (`Point?`);
  `ListView.Viewport.Y` is the scroll offset.
- E2E harness (`tests/ClickUpTodo.Tui.E2E/`) already injects SGR-1006 mouse and
  has a `double_click_check.py` scenario to model the new fold-click scenario on.

## Design

### Phase 1 — expose the marker span + pure column hit-test + share the fold core

- `TaskRowFormatter.Row`: add `MarkerStart`/`MarkerLength` (char offsets of the
  fold marker within `Text`, `(-1, 0)` when no marker), captured incrementally
  exactly like the badge spans so indent/badges/emoji never skew it.
- `TaskRowRenderer.RenderedRow`: pass `MarkerStart`/`MarkerLength` through.
- `RowHitTester.IsWithinFoldMarker(int clickX, string rowText, int markerStart,
  int markerLength, Func<string,int> columnWidth)` → `true` when the click
  column lands within the marker's rendered columns. Pure; `columnWidth` lets
  tests drive both ASCII (`s => s.Length`) and wide-rune cases without
  Terminal.Gui.
- Refactor the shared fold mutation out of `ExpandOrEnter`/
  `CollapseOrJumpToParent` into a private `SetFold(index, expand)` so mouse and
  keyboard converge on one path that keeps `_expanded` + the arranger
  authoritative. No behaviour change to the keyboard path.
- Unit tests: marker span offsets (icons/text/hidden, indented); `IsWithinFoldMarker`
  (hit, miss left/right, wide-rune prefix, absent marker); `SetFold`-backed
  keyboard fold unchanged.

### Phase 2 — wire single-click fold toggle in TodoApp + E2E

- Add a parallel `_markerSpans` (`List<(int Start, int Length)>`) populated in
  `AddTask`/`AddHeader`/`AddSpacer`/`Render` reset and `UpdateTaskRow`, storing
  each row's marker char-span (`(-1, 0)` on non-marker rows).
- Extend `OnListMouse`: keep the double-click → open branch; add a
  `LeftButtonClicked` branch that, gated on `ShowSubtasks && ActiveScreen is
  null`, resolves the clicked row via `RowHitTester.RowIndexAt`, requires
  `_folds[i]` to be `Collapsed`/`Expanded`, and toggles via `ToggleFoldAt(i)`
  only when `IsWithinFoldMarker` (measured with `s => s.GetColumns()`) is true.
  Clicks elsewhere fall through to native selection.
- Add an E2E `fold_click_check.py` scenario: click a collapsed parent's arrow →
  child rows appear; click again → they disappear; click a leaf/title → no fold
  change.

## Invariants

- **No second focusable pane (#3/#38)** — a handler on the existing list only.
- **Mouse is additive** — keyboard fold, single-click select, drag-scroll, and
  A's double-click-open all keep working exactly as today.
- **Bare letters reserved for type-ahead (#12)** — no keyboard change.
- One fold code path (`SetFold`) keeps `_expanded` + arranger authoritative for
  both mouse and keyboard.

## Deferred

Nothing functional. If SGR mouse injection proves flaky in the PTY harness this
session, the `fold_click_check.py` scenario is tracked as a follow-up rather than
blocking the feature (precedent: #333/A), with manual verification described in
the PR.
