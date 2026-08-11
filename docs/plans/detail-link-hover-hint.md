# Task Detail links: hover feedback via a status-line target hint (#408)

Follow-up to #317 / #318, part of epic #313. Deferred from #318's PR.

## The problem #408 poses

#318 shipped the pane-level mouse hit test (`DetailPaneView.LinkAt`) but **not**
hover feedback, and the issue records two reasons hover is subtle:

1. **Hover-to-underline has nothing to do** — #317 already underlines *every*
   link unconditionally (`OnDrawReadOnlyColor` re-resolves each link cell to
   `… | TextStyle.Underline`). Making the underline hover-only would make links
   invisible until pointed at — wrong for a keyboard-first TUI. So hover must be
   *additional* feedback, a fresh styling decision.
2. **The cost lands on a pinned property** — a *cell-restyling* hover cue needs
   `WantMousePositionReports = true` and repaints affected cells on mouse-move.
   Keypress latency and bytes-per-redraw are properties this repo measures and
   defends (#3, the `drive.py` / `screen_check.py` baselines).

## Decision — a status-line hint, not a cell restyle

The issue lists three candidate cues: "reverse video, a brighter foreground, or
a **footer hint naming the target** rather than any cell restyling at all." We
ship the **status-line hint**, for three reasons:

- **It answers concern (2) directly.** It restyles **zero** pane cells, so it
  *cannot* touch the `drive.py` / `screen_check.py` keypress baselines — those
  measure keypress redraws, and a hover hint never runs on a keypress. The only
  redraw a hover ever causes is a single status-label row, and only when the
  hovered target *changes* (crossing a link boundary), never on every move
  within one link.
- **It avoids a visual collision.** A reverse-video hover cue would be
  indistinguishable from the existing keyboard-focus emphasis (#319, also
  reverse-video `Focus` role). A status-line hint is an orthogonal surface.
- **It adds information, hides nothing** — the keyboard-first contract. It reads
  like a browser status bar: point at a link, see where it goes.

We also do **not** enable new terminal traffic: the `ansi` driver already turns
on `?1003h` (any-motion) + `?1006h` at boot, so motion reports already flow to
the app. `WantMousePositionReports = true` on the panes only decides whether
`DetailPaneView` *acts* on events it is already receiving.

## Hit test — reuse the draw path's per-row extraction, not `LinkAt`

The issue asks us to "reuse the existing hit test — `DetailPaneView.LinkAt`."
`LinkAt` is the wrong tool for *continuous* hover: for a modified/​unmapped
position it calls `base.OnMouseEvent(new Mouse { … LeftButtonClicked })` to move
the base view's caret, which on every mouse-move would drag the read-only
caret and could nudge the viewport. That is exactly the churn we're avoiding.

Instead we reuse the **draw path's** own per-row link extraction — the same
source of truth the underline and OSC-8 target already project from:

- `displayRow = Viewport.Y + position.Y`; guard `0 ≤ displayRow < Lines` (the
  #318 "below the body" guard) and `0 ≤ position.X < GetColumnsWidth(row)` (the
  #318 "right of the text" guard). Same two clamping guards `LinkAt` documents.
- Map the mouse **column** to a **cell index** by accumulating per-grapheme
  column widths (wide-rune-safe; URLs are ASCII so it's identity on a link).
- Read the resolved target straight off the already-wrapped row via the existing
  `RowLinkUrls` / cell-URL path (`ClassifyRow`), plus its kind (task vs web).

This adds **no** word-wrap math of our own (it reads `GetLine(displayRow)`, the
row Terminal.Gui already wrapped — the same principle the click hit test and
#413 follow), so it honours "don't add a second mapping." It is pure of caret
side effects and unit-testable headless.

## Surfaces & precedence

- **`DetailPaneView`** gains `HoverTargetUnderMouse(Point, Rectangle)` (pure,
  testable) and, in `OnMouseEvent`, a motion arm (`MouseFlags.ReportMousePosition`,
  no button) that computes the hovered target and raises
  `event EventHandler<HoverTarget?>? HoverTargetChanged` **only when it changes**
  (dedup against the last raised value, including → `null` on leaving a link).
  The motion arm always falls through to `base.OnMouseEvent`; the click path
  (#318) is untouched.
- **`ContextualFooter`** owns hover-vs-flash **precedence** in one unit-testable
  place: a hover hint shows over the steady status line and is restored to the
  steady status on leave; a `Flash` outranks and replaces a hover hint (a hover
  re-asserts on the next move). Hover never clobbers a live flash.
- **`Screen`** base gains `HoverHintChanged` mirroring `FlashRequested`;
  **`TaskDetailScreen`** subscribes each pane's `HoverTargetChanged`, gates it
  the same way link clicks are gated (suppressed while the Dispatch pane /
  comment composer / description editor / reply picker overlay owns input), and
  re-raises it. **`TodoApp`** and **`SingleTaskApp`** route it to the footer,
  exactly as they already route `FlashRequested`.

Hint wording (pinned by tests): a task link → `🔗 Task <id> — Enter/click opens
it here`; a web link → `🔗 <url> — Enter/click opens in browser`. Mirrors what
`LinkActivator.Resolve` actually does on activation, so the hint can't promise a
destination the click won't honour. (Exact copy finalized in Phase 2.)

## Phases

1. **Pure hit test + event (CI-testable).** `HoverTargetUnderMouse` + the
   `HoverTarget` record on `DetailPaneView`; `DetailPaneViewTests` for the
   mapping (on a link, off a link, right-of-text, below-body, wide-rune column,
   task vs web, wrapped continuation row). `ContextualFooter` hover-vs-flash
   precedence + its unit tests. No wiring yet.
2. **Wire-up.** `WantMousePositionReports` + the motion arm + `HoverTargetChanged`
   dedup in `DetailPaneView`; `Screen.HoverHintChanged`; `TaskDetailScreen`
   subscribe/gate/re-raise; both hosts route to the footer.
3. **E2E validation.** A `tui-validate` check (`link_hover_check.py`): move the
   SGR-1006 pointer onto a seeded task link and a web link, assert the status
   line shows the right hint; move within the same link → **no** screen change
   (zero-byte redraw); move off → hint clears; a hint never appears under an
   open overlay. Confirm `link_click_check.py` / `link_tab_check.py` /
   `detail_check.py` A/B unaffected, and that a keypress after hovering still
   redraws at the `screen_check.py` baseline (the #3 invariant).

## Acceptance criteria (from #408)

- Hover feedback ships with `drive.py` / `screen_check.py` at their documented
  baselines while the mouse moves over a pane — satisfied *by construction*
  (zero pane-cell restyle; hover never runs on a keypress), and demonstrated by
  the Phase-3 E2E check.

## Deferred

- A *cell-level* hover emphasis (reverse video / brighter foreground) is
  explicitly **not** pursued — it collides with the #319 focus cue and reintroduces
  the per-cell repaint cost this design avoids. If a future need arises it gets
  its own issue.
