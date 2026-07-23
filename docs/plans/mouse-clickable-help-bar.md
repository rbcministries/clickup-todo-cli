# Mouse/UX (D): clickable contextual help bar (#289)

Part of the Mouse-interaction epic **#283**. Turn the bottom contextual help
footer into a surface where **action** hints fire their shortcut on a
left-click, while **movement** hints stay non-clickable, the current
overflow-hiding is preserved, and the `F1 Help + Shortcuts` fallback is itself
clickable.

## Current state (verified)

- The footer is a **single `Label` `_helpLabel`** owned by `TodoApp`
  (`TodoApp.cs:456`, anchored `Pos.AnchorEnd(1)`), its text rebuilt in
  `UpdateHelpLine()` (`:1170`) and re-fitted on resize via
  `_window.SubViewsLaidOut` (`:468`). It is **not** a Terminal.Gui `StatusBar`.
- Content is modeled by the pure `Tui/Screens/HelpLine.cs`:
  `HelpItem = (Key, Label)`; `Format` joins items with ` · `; `Fit` keeps the
  longest leading prefix that fits alongside a reserved trailing
  `HelpFallback` (`F1 Help + Shortcuts`), column-aware via an injected measure;
  `ForActiveScreen` picks the active screen's set or the list fallback;
  per-screen sets live in `HelpItemSets`.
- **No mouse code exists anywhere in `src/` yet** (`grep Mouse src/` is empty).
  This is the first mouse handler on the main window. The sibling mouse PRs
  (#340 main-list double-click, #341 Quick-Updates click) touch different
  surfaces and introduce the E2E harness's SGR-1006 mouse injection; this issue
  has **no hard dependency** on them (the issue says so) — its feature code
  stands alone.
- Terminal.Gui **2.4.10**. Verified available: `Application.RaiseKeyDownEvent(Key)`
  (static, dispatches to the focused view), `Key.TryParse(string, out Key)`,
  `MouseFlags.LeftButtonClicked`, `MouseEventArgs.Flags` / `.Position`.

## Design

### Convergence seam: re-raise the keyboard chord (no duplicated action logic)

A click on an action hint **synthesizes the same key press** and re-raises it
through `Application.RaiseKeyDownEvent`, which delivers it to the currently
focused view — the main `ListView` (→ `OnListKey`) when no screen is open, or
the active screen's focused control otherwise. Mouse and keyboard therefore
converge on the *existing* handlers with zero duplication, and every screen
gets click-to-fire for free without the host knowing any screen's actions.

Verified with `Key.TryParse`: every chord the sets need parses
(`Ctrl+N`, `F1`–`F12`, `Esc`, `Enter`, `Space`, `Tab`, `Ctrl+Q`, …). The two
exceptions get an explicit chord (below).

### Model changes (`HelpLine.cs`, still Terminal.Gui-free)

- `HelpItem` gains two fields, defaulted so existing 2-arg construction and
  record-equality are unchanged:
  `record struct HelpItem(string Key, string Label, bool IsAction = true, string? Chord = null)`.
  - `IsAction` — `true` for a clickable action; `false` for a non-clickable
    movement/informational hint (arrow/PgUp glyphs, `type to search`). Default
    `true` because footer items are mostly actions; only movement items are
    annotated `IsAction: false`.
  - `Chord` — the raiseable key token when the display `Key` is a glyph or a
    compound label rather than a parseable key. `ActionKey => Chord ?? Key`.
    Needed for: `␣`→`Space`, `↩`→`Enter`, `Enter/Save`→`Enter`,
    `Esc/Enter`→`Esc`, `Del`→`Delete`.
- Two new **pure** helpers (unit-tested, injected column measure):
  - `ColumnRanges(items, measure)` → each item's `[Start, End)` display-column
    span within `Format(items)`, with the ` · ` separators as gaps.
  - `HitTest(items, column, measure)` → index of the item whose span contains
    `column`, else `-1` (a separator, or out of range).

### `HelpItemSets` classification

Annotate movement/informational items `IsAction: false` (they stay leading,
render as plain text, no click): the arrow/PgUp glyph items and `type to
search`. `Tab` items stay actions (a discrete "advance"; re-raising `Tab`
cycles exactly as the key does). Glyph/compound action items get a `Chord`.
Everything else keeps `IsAction: true` implicitly.

### Host (`TodoApp.cs`)

- Keep `_helpLabel` a `Label`; **appearance and text are byte-for-byte
  unchanged**. Cache the *fitted* item list actually rendered in a field
  `_helpFooter` (set alongside `_helpLabel.Text` in `UpdateHelpLine`) so
  hit-testing matches what's on screen at the current width.
- `_helpLabel.MouseEvent += OnHelpBarMouse`. On `LeftButtonClicked`, resolve
  `HitTest(_helpFooter, e.Position.X, s => s.GetColumns())`; if it lands on an
  action item, `Key.TryParse(item.ActionKey, …)` and
  `Application.RaiseKeyDownEvent(k)`, then `e.Handled = true`. A hit on a
  movement item / separator / empty space is left unhandled (native behavior,
  no focus change — `Label` is `CanFocus=false`).

### Invariants preserved

- **No second focusable pane (#3/#38)** — the footer stays the single
  non-focusable `Label`; no new view, `Tab` still cycles only the ListView
  sections. Clicking never moves focus.
- **Bare letters reserved for type-ahead (#12)** — no keyboard change.
- **Generated client / curated spec untouched** — no ClickUp API surface.
- **Footer stays one line**; overflow-hiding via `Fit` is unchanged (measured
  on the same rendered text).

## Deferred (tracked)

- **Label-glyph tightening** (`↑↓` for sort, `👁✅` for Show-Completed, …): the
  issue tells us to *coordinate label/shortcut wording with E (#290, shortcut
  standardization)* so buttons reflect the final unified keys rather than being
  churned twice. Keeping the labels as-is also preserves the #103 byte-for-byte
  footer guarantee (and its pinned test). Deferred to **#290**; noted in the PR.
- (No further deferrals.) A self-contained `tui-validate` scenario
  (`tests/ClickUpTodo.Tui.E2E/help_bar_click_check.py`) is committed with this
  change — it injects SGR-1006 clicks and asserts an action fires, a movement
  hint no-ops, and a double-click fires exactly once. It uses inline SGR bytes
  (like the existing `drive.py`), so it doesn't depend on or conflict with the
  mouse-injection helper the sibling PR **#340** adds to the harness.

## Phases

1. **Model + classification + tests** — extend `HelpItem`, add
   `ColumnRanges`/`HitTest`, annotate `HelpItemSets`, unit tests (incl. a theory
   pinning that every action item's `ActionKey` parses).
2. **Host wiring** — cache fitted footer, add `OnHelpBarMouse`, build.
3. **Finalize** — quality gate, PR, manual-verification notes.
