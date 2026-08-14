# Quick-open launch modes (Ctrl+Enter new tab, Ctrl+Alt+Enter split pane) — #615

Slice **B** of the Ctrl+O quick-open epic (#613). Bring the quick-open entry
surface up to parity with the launch-mode gestures every other task surface
already offers: `Enter` opens in place (unchanged), `Ctrl+Enter` opens the
resolved task in a new terminal tab, `Ctrl+Alt+Enter` opens it in a split pane.

Scope is the **dashboard** host only (`TodoApp`). Single-task mode is slice
**C** (#616); the native-modal re-host is slice **E** (#618) and is a hosting
change only — this slice keeps the surface as the `_screens` `Screen` it is
today, and deliberately keeps the launch **intent in the result** (rather than
firing a per-gesture event) so E's re-host stays a hosting change.

## Acceptance criteria (from the issue)

- `Enter` / `Ctrl+Enter` / `Ctrl+Alt+Enter` on the quick-open surface open the
  resolved task in place / a new tab / a split pane.
- Driver-robust fallback: `New tab` / `Split pane` buttons beside `Open` /
  `Cancel` (Tab-reachable), and clickable footer items, because
  `Ctrl+Enter`-from-a-text-control is not driver-robust (composer precedent,
  #503 open).
- The blank-input guard applies to all three intents (flash + stay open).
- No second focusable pane / latency regression (#3); bare-letter type-ahead
  (#12) intact; #355 cross-check + `tui-validate` green.

## Design

### 1. Result carries the intent (`Tui/Screens/QuickOpenScreen.cs`)

- New `QuickOpenIntent { OpenHere, NewTab, SplitPane }` and
  `readonly record struct QuickOpenRequest(string Text, QuickOpenIntent Intent)`
  with a pure `From(string? rawText, QuickOpenIntent)` factory (trim + blank
  guard → `null`). The factory is the unit-testable seam (no Terminal.Gui).
- `Result` changes from `string?` to `QuickOpenRequest?`. The screen still only
  *collects*; the host owns parse/resolve/navigate/launch.
- Dispatcher gains `OpenInNewTab` / `OpenInSplitPane` entries alongside `Open`.
- `New tab` / `Split pane` buttons added beside `Open` (`IsDefault`) / `Cancel`.
- `Submit(intent)` sets `Result` + closes when `From(...)` is non-null, else
  flashes and stays open.

### 2. Keybindings (`Tui/Screens/Keybindings.cs`)

Two entries beside the existing `ScreenContext.QuickOpen` block, **reusing** the
app-wide `OpenInNewTab` / `OpenInSplitPane` actions (so
`AllBindingsOfAnAction_ShareOneKey` stays green and #506's override layer reads
them with no per-screen change):

```csharp
[(ScreenContext.QuickOpen, KeyAction.OpenInNewTab)]    = "Ctrl+Enter",
[(ScreenContext.QuickOpen, KeyAction.OpenInSplitPane)] = "Ctrl+Alt+Enter",
```

Exact-`KeyCode` dispatch means `Enter` / `Ctrl+Enter` / `Ctrl+Alt+Enter` cannot
collide.

### 3. Footer (`Tui/Screens/HelpLine.cs`)

`HelpItemSets.QuickOpen` gains `Ctrl+↩ new tab` / `Ctrl+Alt+↩ split pane`
(matching the main-list / `DetailWithTaskTree` glyph style). Clickable footer
items re-raise the chord internally, so a mouse user reaches the gesture even on
a driver that eats the chord.

### 4. Host launch path (`Tui/TodoApp.cs`)

- `ShowQuickOpenSurface` reads `QuickOpenRequest?` and routes:
  - `OpenHere` → today's `ResolveAndOpen(text)`, verbatim.
  - `NewTab` / `SplitPane` → `ResolveAndLaunch(text, LaunchLocation)`.
- `ResolveAndLaunch` uses a new pure `QuickOpenParser.ResolveLaunch(universe,
  text)` (no I/O): an unparseable token → `null` (flash, launch nothing); a
  cache hit → the real id + name; a miss → the **raw token** handed to the child
  as both id and display name. The child's `--task` resolves every Ctrl+O form
  (#464), so no parent-side round-trip is needed.
- The launch itself is the existing `LaunchAppForTask(id, name, destination)`,
  which brings the in-flight guard, the #505/#515 split-viability floor (a
  too-narrow split degrades to a tab and says so), the split→tab→window ladder,
  and the clipboard fallback. Nothing new to write there.

## Tests

- `KeybindingsTests` — the two `QuickOpen` tokens; existing cross-checks
  (`Footer_ShowsTheTableKey_ForEveryBinding`, `AllBindingsOfAnAction_ShareOneKey`,
  `EveryToken_IsParseable`) auto-cover the rest.
- `HelpLineTests` — updated pinned `QuickOpen` footer format;
  `EveryActionItem_ReRaisesAParseableKey` covers the two new chords.
- `QuickOpenRequestTests` — `From` yields the right intent + trimmed text for
  each gesture; blank/whitespace yields `null`.
- `QuickOpenParserTests` — `ResolveLaunch`: cache hit → real id+name; miss →
  raw token as id+name; invalid → null.
- `tui-validate` — footer renders both new items; the buttons render.

## Non-goals / deferred

- Single-task mode (**C**/#616) and the Feed host (epic non-goal).
- Any hosting-mechanism change (**E**/#618 re-hosts as a native `Dialog`).
- Rebindable chords (#506).
