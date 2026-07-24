# Task Detail (D): mouse click activation of in-pane links

Issue: [#318](https://github.com/rbcministries/clickup-todo-cli/issues/318) ·
Epic: [#313](https://github.com/rbcministries/clickup-todo-cli/issues/313)

Builds on **B** (link model, #316) and **C** (link styling, #317). Produces the
**link-activation dispatcher** that **E** (keyboard traversal, #319) and **F**
(configurable Ctrl+Click destination, #320) both consume.

## Goal

Clicking a link in the Description / Comments / Stream panes acts on it:

| Gesture | Task link (`app.clickup.com/t/…`) | Web link |
| --- | --- | --- |
| Plain click | open that task's **Task Detail** in-app | open in the **browser** |
| `Ctrl`+click | open in the **browser** | open in the **browser** |

`Ctrl+Click` is Windows Terminal's own "open this link" gesture, so it stays the
one gesture that always means *browser*, whatever the link kind.

## Verified current state

- **B** ships `TaskLinkExtractor.Extract(text)` → ordered, non-overlapping
  `LinkSpan`s (char offsets into the string passed in, `Kind`, `Url`, `TaskId`,
  `IsCustomTaskId`). **C** ships `DetailPaneView.BuildCells`, which extracts
  links **per source line** and tags their cells — so a line's spans are already
  the unit the pane thinks in.
- Mouse is established elsewhere in the app (`TodoApp.OnListMouse`,
  `TaskDetailScreen.OnTreeMouse`, `HelpLine.HitTest`), with `MouseFlags.Ctrl`
  the existing modifier idiom (`Ctrl`+click launches a task tab, #301). The
  detail **panes** have no mouse handling yet.
- Destinations already exist in both hosts: `TodoApp.LaunchBrowser(url, name)`
  (rewrite → parse → `IBrowserLauncher`, #304/#346) and
  `TodoApp.ResolveAndOpen(text)` (#303/#353), which already accepts a **ClickUp
  task URL** and handles cache-first opens, plain ids, and custom ids (with the
  URL's own team id) via `OpenTaskDetail`. `SingleTaskApp` has its own
  `LaunchBrowser` but does **not** wire `OpenTaskRequested` — single-task mode
  has no in-app task→task navigation yet (#374).

## The hard part: a click coordinate → a `LinkSpan`

The panes are `WordWrap = true` `TextView`s, so screen row ≠ source line and
screen column ≠ char offset. Terminal.Gui's `WordWrapManager` (which owns the
wrapped→model mapping) is `internal`, and re-implementing its wrap is exactly
the kind of drift this repo avoids.

It doesn't have to be re-implemented. `TextView` **publishes** the mapping:
`OnUnwrappedCursorPositionChanged(Point)` reports the caret "in unwrapped model
coordinates", and it is raised **synchronously while the base view handles the
click**. Probed against Terminal.Gui 2.4.10 (throwaway harness, results folded
into the tests below):

- A click at viewport `(x, y)` yields `(X = cell index within the source line,
  Y = source line index)` — the mapping is exact even when a URL is itself split
  across a wrap, and it already accounts for the pane's scroll offset.
- It re-fires on every click, including a repeated click at the same position,
  so a cached "last unwrapped position" is never stale.
- `X` is a **cell (grapheme) index**, not a UTF-16 char offset — for
  `"ab 😀😀 https://x.com/y"` a click on the URL's `h` reports `X = 6` where the
  char offset is `8`. `Cell.ToCellList(line, null)` produces byte-for-byte the
  same graphemes as `TextView`'s own model (verified), so summing grapheme
  lengths converts cell index → char offset exactly.
- `TextView` only maps positions for an **unmodified** click: its handler tests
  `Flags == LeftButtonClicked`, so a `Ctrl`+click leaves the caret (and the
  reported position) **stale**. The fix is to resolve the position by handing the
  base a synthesized *plain* click at the same point, then act on the modifiers
  ourselves. Harmless — the panes are read-only, so a caret move is invisible.

Two guards are required, both from probed false-positive cases:

1. **Below the content.** A click in the empty area under a short body clamps to
   the *last* line, honouring the clicked column — so clicking blank space under
   a body whose last line ends in a URL would "hit" that URL. Reject when
   `Viewport.Y + y >= Lines` (wrapped line count).
2. **Right of the text.** A click right of a wrapped row's text clamps to the end
   of that row, which — when the source line continues past the wrap — is the
   *next* character. Probed: on `"short https://…/t/abc123 tail"` wrapped at 30
   columns, a click at column 25 of the row showing `"short "` resolved to offset
   6, the URL's first char. Reject when `x` is beyond the clicked row's rendered
   width (`GetColumnsWidth(GetLine(row))`, column-aware so wide runes count).

A click that lands on the exclusive `End` of a span (i.e. just past the last
character) is not a hit — the span test is `Start <= offset < End`.

## Design

### 1. `Tui/LinkActivation.cs` — the dispatcher (new, pure, Terminal.Gui-free)

```csharp
public enum LinkAction { OpenInBrowser, OpenTaskDetail }

public readonly record struct LinkActivationRequest(LinkSpan Span, LinkAction Action)
{
    public string Url => Span.Url;
}

public static class LinkActivator
{
    // (LinkSpan, modifiers) → action. Ctrl always means browser; a plain click follows the
    // kind. #320 extends this arm with the configurable task destination + Shift inversion.
    public static LinkAction Resolve(LinkSpan span, bool ctrl);

    // The span containing a char offset, or null. Start <= offset < End.
    public static LinkSpan? SpanAt(IReadOnlyList<LinkSpan> spans, int charOffset);

    // Cell (grapheme) index → UTF-16 char offset, for the cell-indexed position TextView reports.
    public static int CharOffsetAtCell(IReadOnlyList<string> graphemes, int cellIndex);
}
```

Shared with **E**: the keyboard path resolves a focused span through the same
`Resolve`, so click and `Enter` can't drift.

### 2. `Tui/DetailPaneView.cs` — the mouse seam

- `SetBody` remembers the body's lines + separator (it already splits them for
  `BuildCells`), so a click can re-extract the clicked line's spans — one short
  regex per click, no per-render cache to invalidate.
- Cache the unwrapped caret from an `OnUnwrappedCursorPositionChanged` override.
- Override `OnMouseEvent`: on `LeftButtonClicked` (with or without `Ctrl`), apply
  the two guards, resolve the position — an unmodified click goes to the base
  first and its own mapping is read back; only a *modified* click needs the
  synthesized stand-in, so the common gesture stays a single pass through the
  base — convert cell index → char offset, hit-test the clicked line's spans, and raise
  `event EventHandler<LinkActivationRequest>? LinkActivationRequested`. Anything
  else (no hit, wheel, drag, double-click) falls through to `base`, so native
  caret/selection/scroll behaviour is untouched.
- Separator lines are skipped exactly as `BuildCells` skips them.

No new focusable view, no second pane — the panes are already the tab's content
view, so the #3 single-focus input model is untouched.

### 3. `Tui/Screens/TaskDetailScreen.cs`

Subscribes all three panes, guards on no overlay being open (`_commentBox` /
`_descriptionBox` / `_promptBox` — the same "the overlay owns input" rule `OnKey`
uses, so a click can't navigate away from an open draft), and re-raises one
screen-level `LinkActivationRequested` for the host.

### 4. Hosts

- `TodoApp`: `OpenInBrowser` → `LaunchBrowser(url, Ellipsize(url))`;
  `OpenTaskDetail` → `ResolveAndOpen(url)`, which already covers cache-first,
  plain-id, and custom-id (`/t/{teamId}/{customId}`) links through one tested
  resolution path — no new API code and no duplicate custom-id logic.
- `SingleTaskApp`: both actions open the browser, because single-task mode has no
  in-app task→task destination yet (#374). Unlike `Ctrl+B` this does **not**
  close the tab, so it flashes the outcome on the live footer.

## Deliberately not in this slice

- **Hover feedback.** #317 shipped an *unconditional* underline on both link
  kinds precisely because hover wasn't available, so hover-to-underline would now
  be visual churn; a distinct hover emphasis needs
  `WantMousePositionReports = true`, which turns every mouse move into a
  hit-test + repaint on a UI whose input latency and output volume are pinned
  properties (#3). The hit-test seam this slice adds is the prerequisite; the
  styling decision is tracked in **#408**.
- **OSC-8 hyperlinks** — #380.
- **Configurable Ctrl+Click destination / Shift inversion** — #320 (the
  `Resolve` arm is left as the extension point).
- **Keyboard link traversal** — #319 (consumes `LinkActivator`).

## Test plan

### Unit — `tests/ClickUpTodo.Tests/LinkActivationTests.cs` (new)

`Resolve` for every (kind × ctrl) combination incl. custom-id task links;
`SpanAt` boundaries (`Start` hit, `End - 1` hit, `End` miss, gaps, empty list,
negative offset); `CharOffsetAtCell` over ASCII, astral (surrogate-pair) and
combining-mark graphemes, plus clamping at both ends.

### Unit — `tests/ClickUpTodo.Tests/DetailPaneViewTests.cs`

Real `DetailPaneView` instances, laid out and clicked through the public
`View.NewMouseEvent` — no `Application` / driver needed, so these run in CI and
pin the actual Terminal.Gui behaviour the design rests on:

- plain click on a task link → `OpenTaskDetail` with that span; on a web link →
  `OpenInBrowser`;
- `Ctrl`+click on **either** kind → `OpenInBrowser` (this is the case a stale
  caret would silently get wrong);
- a click on ordinary text, on the separator rule, **below the last line**, and
  **right of a wrapped row's text** → no event (the two probed false positives);
- a click on a link on a **wrapped** line, and after **scrolling**, still
  resolves the right span;
- a link containing/preceded by wide (emoji) runes resolves by grapheme, not by
  char offset.

Each guard is pinned by a test that fails without it (verified by removing each
guard in turn: `Click_RightOfAWrappedRowsText_ActivatesNothing` and
`Click_BelowTheLastLine_ActivatesNothing` are the ones that catch it).

### E2E — `tests/ClickUpTodo.Tui.E2E/link_click_check.py` (new)

The harness already seeds a task link in the description and a web link in a
comment (#317's `link_check.py`). Drive real SGR mouse clicks under the PTY and
assert the resulting navigation: plain click on the description's task link →
Task Detail for that task; `Ctrl`+click → no navigation and the footer reports
the browser open (the fake `IBrowserLauncher` in the harness records the URL);
plain click on the comment's web link → same browser report, no navigation.

### Regression

`detail_check.py`, `link_check.py`, `tree_tab_check.py`, `drive.py` (latency) and
`screen_check.py` (output volume) unchanged — this slice adds no draw work and no
mouse position reporting.
