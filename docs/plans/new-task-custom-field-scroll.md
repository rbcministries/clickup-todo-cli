# Plan — Scroll the New Task Custom fields page when the field stack exceeds the screen (#446)

Follow-up to #395 (PR #445), which added the New Task screen's **Custom fields** page (page 2): a
top-down stack of one input widget per fillable field, above a shared Save/Back button row. This
issue closes the deferred edge case noted in `new-task-custom-field-widgets.md`: a list with **many**
fillable fields (or several `drop_down`/`labels` fields, each multiple rows) can produce a stack
taller than the screen, so the lower widgets are clipped and unreachable on a small terminal.

## What's already true (no change needed)

- The pure collection logic (`NewTaskCustomFieldForm` + `CustomFieldValueSerializer` +
  `CustomFieldRequiredValidator`) is layout-agnostic — untouched.
- The shared **Save / Cancel(Back)** button row is anchored to the screen bottom
  (`Pos.AnchorEnd(1)`) and is **not** a child of `_fieldsPage`; it stays visible and reachable
  regardless of field count. So only the widget stack **inside** `_fieldsPage` needs to scroll.

## The gap

`NewTaskScreen.BuildFieldWidgets` lays widgets out top-down from a running `y` into `_fieldsPage`
(a `View`, `Height = Dim.Fill(2)`). When the final `y` exceeds the page's viewport height, the
trailing widgets fall below the fold — clipped, un-focusable in practice (nothing scrolls them into
view), so required fields there can neither be seen nor filled.

## Chosen shape — Terminal.Gui content-scroll on `_fieldsPage`

Terminal.Gui v2 makes `_fieldsPage` itself the scroll viewport over a taller content area:

1. **Declare the content size.** After building the widgets, `SetContentSize(new Size(w, H))` where
   `H` is the stacked content height (the final `y`) and `w` the page's current viewport width. This
   tells TG the content extends past the viewport, enabling scrolling.
2. **Built-in auto vertical scrollbar.** `ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar`
   (Auto mode): a visible affordance + mouse-wheel scrolling, **hidden automatically when the content
   fits** — so short field lists (and the existing `new_task_custom_fields_check.py`, which runs at a
   tall `ROWS=50`) are byte-for-byte unaffected.
3. **Scroll-on-focus (the reachability guarantee).** Each focusable widget's `HasFocusChanged` nudges
   the viewport so the newly-focused widget's content-row range is visible. This makes the existing
   **Tab order** reveal every widget — Tab past the fold scrolls the next field into view — which is
   what actually guarantees "every widget stays reachable" for a keyboard-first TUI.
4. **PgUp/PgDn** scan the page without moving focus (the issue's explicit suggestion). Guarded so a
   focused `drop_down` `ListView` keeps its own page-selection behaviour (we don't hijack PgUp/PgDn
   when the key's origin is a ListView).

Why content-scroll rather than a second inner scrollable pane: it keeps the single-screen, single
focus-chain model (no new focusable *list* pane — #3 is about the main task ListView, and this is a
modal, but we still avoid a second competing scroll region), keeps the existing Tab order intact, and
reuses TG's own viewport clamping.

## Pure, unit-tested arithmetic — `NewTaskFieldsScrollModel`

The clamp / ensure-visible math is factored out of the Terminal.Gui glue (same pure-glue split as
`DetailScrollModel`) so it's unit-testable without a terminal:

```
NewTaskFieldsScrollModel.ClampTop(desiredTop, contentHeight, viewportHeight) -> int
    // clamp to [0, max(0, contentHeight - viewportHeight)]

NewTaskFieldsScrollModel.ScrollToShow(currentTop, itemTop, itemHeight, viewportHeight, contentHeight) -> int
    // minimal new top so [itemTop, itemTop+itemHeight) is visible; an item taller than the
    // viewport aligns to its top (so its label/first row shows); result clamped to bounds.
```

The screen keeps only the glue: read `_fieldsPage.Viewport` / `GetContentSize()` / the focused
widget's `Frame`, call the model, and assign `_fieldsPage.Viewport` (a `Rectangle`, mirroring the
`TaskDetailScreen` pane-scroll idiom) when the top changes.

## Phases

1. **Pure + tests (CI-green).** `NewTaskFieldsScrollModel` + `NewTaskFieldsScrollModelTests`
   (clamp bounds; scroll-up/down minimal moves; already-visible no-op; item taller than viewport;
   degenerate heights). Build/test/format green → draft PR.
2. **TUI wire-in (build + `tui-validate`).** `_fieldsPage` content size + auto scrollbar; scroll-on-
   focus on every built widget; PgUp/PgDn in `OnKey` (ListView-guarded). No change to the base page,
   the create path, or the button row.
3. **E2E.** A `E2E_CUSTOM_FIELDS_MANY=1` seeding in the fake backend (a field set tall enough to
   overflow a short terminal) + `new_task_custom_field_scroll_check.py`: boot at a short `ROWS`, open
   New Task, Save → Custom fields page, Tab down to a field seeded below the fold and assert it
   renders (scroll-on-focus), fill the required field, and create — asserting the value round-trips.

## Verification

- `dotnet build -c Release` (0/0), `dotnet test -c Release` (green; integration self-skips),
  `dotnet format`.
- `tui-validate`: the new scroll check, plus `new_task_check.py` and
  `new_task_custom_fields_check.py` still green (short-field lists unaffected).

## Hard rules honored

- **No `Generated/` hand edits; no spec change / no regen** — pure UI + a pure arithmetic helper.
- No generated type escapes the facade (this change is entirely view-layer + a pure model).
- Single sectioned main-list `ListView` model untouched; no second focusable pane on the main list
  (#3/#38). Keypress latency unaffected (no mouse-position reports; scroll-on-focus fires only on
  focus changes, PgUp/PgDn only on those keys).

## Deferred / out of scope

- Terminal **resize** reflow of the content width (vertical scroll is the concern here; horizontal
  scrolling is not enabled). Resize is a documented human-pass item in the `tui-validate` skill.
