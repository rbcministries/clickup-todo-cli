# Open-in-split-pane action + Ctrl+Alt+Enter gesture (#507, split-pane epic E)

Slice **E** of the split-pane epic (#502). Adds the user-facing gesture that
opens the current task in a **split pane** beside the one you're in — the
sibling of the shipped `OpenInNewTab` (`Ctrl+Enter`, #301/#384/#435), not a mode
of it. Default chord: **`Ctrl+Alt+Enter`** (the default the epic already commits
to in its "Why Ctrl+Alt+Enter and not Alt+Enter" section).

## What's already on `main` (this slice only wires them together)

- **B (#504)** — `LaunchLocation.SplitPane` in the planner + the silent
  split → tab → window degradation ladder; `AppHostLaunch` was generalised off
  the tab-only helper to take a `LaunchLocation`, so its options + status
  strings already speak split/tab/window. **Merged.**
- **C (#505)** — `SplitViability.Evaluate(cols, direction, sizePercent)`: the
  pure floor that degrades a `SplitPane` to a `NewTab` *before* planning when the
  resulting pane would be too narrow to read. **Merged.**
- **J (#515)** — the dispatch path already reads the live terminal width
  (`Application.Driver?.Cols`) and feeds the floor in `DispatchCoordinator`.
  **Merged** — the same width read is reused here for the app-host task launch.

So E adds no launch logic. It is: one new `KeyAction`, one table binding, two
footer hints, one screen event + key branch, and the host subscribers — each
mirroring the existing `OpenInNewTab` seam exactly, because the issue's governing
instruction is "bound in the same places `OpenInNewTab` is, so the two gestures
stay symmetric."

## Relationship to the spike A (#503) — deferred verification, not a decision gate

A (#503) is the epic's manual/real-terminal reachability spike; its remaining
open half is confirming `Ctrl+Alt+Enter` arrives at `OnKey` across the three
drivers and six split hosts. That is **verification**, tracked in the epic's
validation gate **#511** — not a product decision (the epic already fixes the
default). This slice follows the precedent B (#504) set: land the CI-verifiable
half now and leave the manual real-terminal confirmation to #511. The chord is
well-grounded — `Ctrl+Enter` and `Ctrl+Alt+R` both already reach `OnKey`, so
`Ctrl+Alt+Enter` is the intersection of two proven cases — and because the chord
resolves through the `Keybindings` table, a later finding that a specific host
eats it is a one-line default change, not a structural one (and slice D/#506's
override layer will let a user rebind it regardless).

## Changes

### 1. `Tui/Screens/Keybindings.cs`
- `KeyAction.OpenInSplitPane` (beside `OpenInNewTab`).
- `[(MainList, OpenInSplitPane)] = "Ctrl+Alt+Enter"`. `Ctrl+Alt+Enter` parses to
  a distinct `KeyCode` (`Enter | Ctrl | Alt`) from `Ctrl+Enter`, so the two never
  collide in the dispatcher. Not bound in the `Detail` table (mirroring
  `OpenInNewTab`, which the Detail path handles directly in `TaskDetailScreen`).

### 2. `Tui/Screens/HelpLine.cs`
- `MainList` and `DetailWithTaskTree` each gain
  `new("Ctrl+Alt+↩", "split pane", Chord: "Ctrl+Alt+Enter")`, placed right after
  the `new tab` item. The `ActionKey` is `Ctrl+Alt+Enter`, which
  `EveryActionItem_ReRaisesAParseableKey` proves parseable. The leaner `Detail`
  set (no tree loader) does **not** carry it, exactly as it doesn't carry
  `new tab` — the gesture is `_treeList`-gated.

### 3. `Tui/Screens/TaskDetailScreen.cs`
- `OpenInSplitPaneRequested` event (sibling of `OpenInNewTabRequested`).
- An `OnKey` branch: `Ctrl+Alt+Enter` (`key.IsCtrl && key.IsAlt && (KeyCode &
  ~(Ctrl|Alt)) == Enter`), gated identically on `!_promptBox.Visible &&
  _treeList is not null`, raising the event. The existing `Ctrl+Enter` branch is
  inert for `Ctrl+Alt+Enter` (its `& ~CtrlMask == Enter` fails when Alt is set),
  so ordering is safe.

### 4. `Tui/TodoApp.cs` and `Tui/SingleTaskApp.cs`
- Generalise `LaunchAppTabForTask(taskId, name)` → `LaunchAppForTask(taskId,
  name, LaunchLocation destination)`; the tab wrappers pass `NewTab`. The single
  in-flight guard (`_launchingTab`) is reused (a launch is a launch).
- **Viability floor**: for a `SplitPane` request, read `Application.Driver?.Cols`
  and call `SplitViability.Evaluate(cols, options.SplitDirection,
  options.SplitSizePercent)` (Auto/even — the geometry `AppHostLaunch.Options`
  implies), degrading to `NewTab` when the pane would be too narrow and appending
  the decision's `Reason` to the success flash. A headless/null-Cols path or a
  non-split request passes through untouched. This is the "tell the truth about
  degradation … including when C's viability floor caused it" requirement.
- **Subscribers/dispatch** (mirroring `OpenInNewTab`):
  - `TodoApp` main list: `.On(OpenInSplitPane, () =>
    LaunchTaskInSplitPane(CurrentTask()))`.
  - `TodoApp` detail: `screen.OpenInSplitPaneRequested += … LaunchAppForTask(
    resolvedId, screen.Task.Name, SplitPane)`.
  - `SingleTaskApp` detail: `screen.OpenInSplitPaneRequested += …
    LaunchAppForTask(tab.TaskId, tab.Task.Name, SplitPane)`.

## Tests

- `KeybindingsTests`: `OpenInSplitPane` token is `Ctrl+Alt+Enter`; it parses; the
  footer cross-check (`Footer_ShowsTheTableKey_ForEveryBinding`) and
  `AllBindingsOfAnAction_ShareOneKey` auto-cover the new binding; an explicit pin
  that `Ctrl+Enter` (new tab) and `Ctrl+Alt+Enter` (split) are distinct actions.
- `HelpLineTests`: update the pinned `Format_MainList_RendersTheFullFooter`
  string; add split-pane item tests mirroring the `new tab` ones (glyph key
  `Ctrl+Alt+↩`, `ActionKey` `Ctrl+Alt+Enter`, clickable) on `MainList` and
  `DetailWithTaskTree`; assert the leaner `Detail` set omits it.
  `EveryActionItem_ReRaisesAParseableKey` covers the chord's parseability.

## Not unit-testable in CI (per CLAUDE.md)

The `OnKey` dispatch and the host launch are Terminal.Gui-thread concerns. Verify
by build + reasoning and, where the harness allows, a `tui-validate` leg;
describe manual verification in the PR. No second focusable pane is introduced
(single sectioned `ListView`/tab model unchanged); the chord is a modified key,
so the ListView type-ahead reservation (#12) is intact.

## Deferred / tracked elsewhere

- Real-terminal `Ctrl+Alt+Enter` reachability + the split-host validation matrix
  → the spike **#503** and the epic validation gate **#511**.
- User-rebindable launch chords (the override layer) → slice **D/#506**; this
  slice ships the static default the table resolves.
