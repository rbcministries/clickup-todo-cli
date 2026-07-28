# Task Detail link focus traversal + keyboard activation (#319, epic #313 — E)

Make `Tab` / `Shift+Tab` step a **visible focus highlight** across the clickable
links in the front-most text pane (Description / Comments / Stream), and `Enter`
activate the focused link — the keyboard equivalent of the mouse click that
landed in #318, satisfying epic #283's "mouse never replaces the keyboard" rule.

## Dependencies (all landed on `main`)

- **A — Tab freed** (#315): tab switching now lives on `Ctrl+←/→`
  (`DetailTabNav` + `TaskDetailScreen.CycleTab`); the comment at
  `TaskDetailScreen.cs:904-908` explicitly frees bare `Tab`/`Shift+Tab` "for
  in-pane link focus traversal (#319, E)".
- **B — link spans** (#316/#317): `TaskLinkExtractor.Extract(line)` yields
  ordered, non-overlapping `LinkSpan`s; `DetailPaneView.BuildCells` already tags
  each link's cells by kind.
- **D — activation dispatcher** (#318): `DetailPaneView.LinkActivationRequested`
  → `TodoApp.ActivateLink` / `SingleTaskApp.OpenLink`, with the pure
  `LinkActivator.Resolve(span, ctrl)` mapping a span + modifier to a
  `LinkAction`. `LinkActivation.cs:16` already anticipates "the keyboard path
  (#319), so both gestures reach a host through one payload."

The keyboard path therefore **reuses the same event and the same pure
`Resolve`** — click and `Enter` cannot drift apart.

## Design

Keep link focus entirely inside `DetailPaneView`, mirroring how it already owns
mouse activation (`OnMouseEvent`). The screen owns tab switching; the pane owns
everything about a link once a text tab is front-most. This keeps the new
behavior CI-unit-testable through the same driver-free path the #318 mouse tests
use (`new DetailPaneView { Frame = … }` → `SetBody` → drive → assert), with only
the thin screen glue left to `tui-validate`.

### 1. Ordered per-pane link list (pure)

Add a pure static `DetailPaneView.ExtractPaneLinks(string body, string separator)`
returning an ordered `IReadOnlyList<PaneLink>` where
`PaneLink = (int LineIndex, LinkSpan Span)` — every link in every non-separator
source line, in document order (line ascending, then span order within a line).
Skips separator lines exactly as `BuildCells` does, so focus can never land on a
link the renderer never drew. `SetBody` caches the result in a `_paneLinks`
field and resets `_focusedLinkIndex = -1` (a re-render — refresh, stream-sort
toggle — invalidates any prior focus).

### 2. Focus index math (pure)

Add a tiny pure helper (repo pattern: `DetailTabNav.NextTab`,
`DispatchPaneModel`) — `LinkFocus.Step(int current, int count, bool forward)` —
returning the next focused index with **wrap-around** (last→first, first→last),
`-1` when `count == 0`, and `0`/`count-1` as the entry point when `current == -1`
(first `Tab` focuses the first link, first `Shift+Tab` the last). Wrap-around is
the documented choice (acceptance leaves "wrap or stop-at-ends" open): there is
nothing else on a text tab to `Tab` to, so cycling is the least surprising.

### 3. Focus highlight (draw)

Add a third sentinel `DetailPaneView.FocusedLinkMarker` (opaque background,
distinct from `TaskLinkMarker`/`WebLinkMarker`/`SeparatorMarker`) and a
`DetailCellStyle.FocusedLink` value in `ClassifyCell`, so the tagging stays
purely unit-tested. Extend `BuildCells` with an optional focused-link parameter
(`PaneLink? focused`): the focused span's cells get `FocusedLinkMarker` instead
of the kind marker; everything else is unchanged. `OnDrawReadOnlyColor` renders a
`FocusedLink` cell in the pane's **Focus** visual role (theme-aware reverse-video
emphasis) + underline — clearly distinct from the always-on link underline (#317
underlines *every* link, so the focus indicator must be *additional* emphasis,
not the underline itself — the exact point #408 makes).

Because the draw override keys off each cell's own attribute (never off wrapped
coordinates — the whole reason #317/#318 tag cells rather than map rows), showing
focus means re-tagging the focused link's cells. On a focus change the pane
rebuilds its cells with the new focused link and reloads them.

### 4. Keep the focused link visible (scroll)

`TextView.Load` homes the viewport, so after a focus reload the pane restores the
scroll so the focused link's row is visible: if the focused link's source line is
above/below the current viewport, scroll to bring it into view (reusing the
base view's own wrap-aware scrolling — `ScrollTo` / `MoveHome` / `MoveEnd`, as
`FlushStreamAutoScrollIfActive` does — never a re-implementation of word wrap).
Exact mechanism (in-place cell mutation vs. reload-then-scroll, and sync vs.
`Application.Invoke` deferral for the post-layout viewport move) is settled in
implementation against the real API; the pure pieces (§1–§3) are fixed here.

### 5. Key routing + activation

Route keys from `TaskDetailScreen.OnKey` — the screen's existing key handler,
which already owns `Ctrl+←/→` tab cycling, the Task Tree tab's `Enter`, and every
command chord, and is wired to each pane's `KeyDown` (so it fires *before*
Terminal.Gui's focus-traversal bindings and can consume bare `Tab`). This keeps
key routing where the screen already centralises it and leaves the pane owning
only the link state, rendering, and activation event (all driver-free
unit-testable). When the front-most tab's scroll target is a `DetailPaneView`:

- `Tab` / `Shift+Tab` → `pane.StepLinkFocus(forward)`. It returns **true**
  (→ `key.Handled = true`, consuming the key so focus traversal doesn't run) when
  the pane has ≥1 link, and **false** when it has none — leaving `Tab` unhandled
  so it falls through exactly as today (acceptance: "if a pane has no links,
  `Tab` does nothing / falls through").
- `Enter` → `pane.ActivateFocusedLink()`, which raises the existing
  `LinkActivationRequested` with `LinkActivator.Resolve(span, ctrl: false)`
  (task→detail, web→browser — identical to a plain click) and returns **true**
  when a link is focused. Returns **false** (falls through) when none is focused,
  so `Enter` on the Task Tree tab (`TaskDetailScreen.cs:799`) and on an unfocused
  pane is undisturbed.

Modifier variants are **out of scope**: a browser-force keyboard gesture on a
*task* link is #320 (F)'s configurable modifier matrix, `Ctrl+Enter` is #384's
new-terminal-tab gesture, and `Shift+Enter` is unreliable (many terminals don't
distinguish it from `Enter`). Plain `Enter` already satisfies the acceptance
criterion for both link kinds. The screen's existing overlay guard
(`OnPaneLinkActivation` suppresses while an overlay owns the keyboard) stays
authoritative; the pane isn't focused while an overlay is up, so no `Tab`/`Enter`
reaches it then anyway.

## Phases

1. **Pure core + tests** — `ExtractPaneLinks`, `LinkFocus.Step`,
   `FocusedLinkMarker` + `DetailCellStyle.FocusedLink` + `BuildCells(focused)`
   overload, with `DetailPaneViewTests` / a new `LinkFocusTests` covering
   ordering, separator-skipping, wrap-around, empty-pane, and the focused-cell
   tagging. `dotnet test` green.
2. **Pane behavior + tests** — `_paneLinks`/`_focusedLinkIndex` +
   `StepLinkFocus`/`ActivateFocusedLink` (public, driver-free), focus-highlight
   reload, and scroll-to-visible. Driver-free `DetailPaneView` tests (à la the
   #318 mouse tests): stepping moves focus across links, wraps, tags the right
   cells, scrolls a long body to keep focus visible, raises
   `LinkActivationRequested` with the `Resolve`-correct action on `Enter`, and is
   inert with no links / no focus.
3. **Screen wiring + docs + E2E** — route `Tab`/`Shift+Tab`/`Enter` from
   `TaskDetailScreen.OnKey` to the active text pane. Update `README.md` Task
   Detail shortcut table (`Tab`/`Shift+Tab` step links, `Enter` activates the
   focused link). Add `tests/ClickUpTodo.Tui.E2E/link_tab_check.py`
   driving `Tab`/`Shift+Tab`/`Enter` over the fake backend and asserting the
   focus highlight moves and `Enter` navigates (task link → detail). Run the
   `tui-validate` skill **after** `dotnet test` is green.

## Testing

- **Unit (xUnit, no driver):** pure link ordering, focus-index wrap math,
  focused-cell classification, and the full pane behavior (focus movement,
  scroll, activation event) via `DetailPaneView` + `NewKeyDownEvent`, mirroring
  `DetailPaneViewTests`' mouse suite. Activation correctness is anchored on the
  already-tested `LinkActivator.Resolve`, so `Enter`/click cannot diverge.
- **E2E (`tui-validate`):** `link_tab_check.py` for the visible focus highlight,
  scroll, and navigation glue that isn't unit-testable in CI.
- No new ClickUp boundary, so no integration/`SkippableFact` test is needed.

## Non-goals / deferred

- The full task-link modifier matrix (`Ctrl`/`Ctrl+Shift` → browser vs. new
  terminal tab, "Shift inverts") is **#320 (F)**; this issue ships only plain
  `Enter` (see §5 — a browser-force keyboard modifier is left to #320, and
  `Shift+Enter` is unreliable across terminals).
- Hover feedback on links is **#408**; OSC-8 hyperlinks are **#380/#430** — all
  independent of focus traversal.
- `Ctrl+Enter` (open current task in a new terminal tab) is **#384**.
