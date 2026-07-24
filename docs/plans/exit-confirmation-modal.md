# Exit confirmation on quit from a root view

Issue: [#299](https://github.com/rbcministries/clickup-todo-cli/issues/299) (Multi-tab
sub-issue 7, epic [#292](https://github.com/rbcministries/clickup-todo-cli/issues/292)).
Depends on sub-issue (6) [#298](https://github.com/rbcministries/clickup-todo-cli/issues/298)/#401
(`NavigationHistory<T>` + the `RequestExit()` seam) — **merged**, so the plug point this
builds on already exists in both hosts. Directly addresses the `Esc` ambiguity flagged in
[#290](https://github.com/rbcministries/clickup-todo-cli/issues/290).

## Goal

Guard the one genuinely destructive `Esc` — exiting the app — with a keyboard-first
confirmation, identically in both launch modes. `Esc`/quit **from a root view** asks
*"Are you sure you want to exit?"*; `Y`/`Enter` exits, `Esc`/`N` cancels and returns to
exactly where the user was. Non-root `Esc` still navigates back with no modal.

## Why this shape

- **`RequestExit()` is already the single chokepoint.** #298/#401 funnelled every root-level
  quit path through `TodoApp.RequestExit()` (the `KeyAction.Quit` binding, `Esc`, `Ctrl+C`) and
  `SingleTaskApp.RequestExit()` (the launch-task detail's `Esc`/close), explicitly as this
  issue's plug point. So the whole feature lands *inside* those two one-line methods — no new
  key wiring, no new Esc paths, and "consistent across launch modes" comes for free because
  both hosts mount the same screen and render the same footer set.
- **A transient modal on the existing `_screens` view-stack**, per the navigation ADR
  (`docs/navigation-model.md`): the exit confirm is an overlay-and-dismiss surface, never a
  `NavigationHistory` entry. Both hosts already have a `ShowScreen`/`CloseScreen` seam that
  hides the layer beneath and restores it on close, so a `Screen` subclass needs no new
  mounting machinery in either host and cannot regress the single-`ListView` input model
  (#3/#38 — one focusable surface at a time, no nested `Application.Run` loop).
- **Keyboard-only, no buttons.** The prompt is a `Label` plus the shared contextual footer;
  the screen owns one `KeyDown` handler. No extra focusable views, so there is nothing for
  focus/latency to go wrong with — and because footer action hints are clickable (#289), the
  dashboard gets a mouse affordance for free without this screen knowing about the mouse.
- **Pure decision logic in `ExitConfirmModel`**, glue-free and unit-tested, mirroring
  `DescriptionEditorModel` / `PromptTemplateEditor`: the Terminal.Gui handler classifies a key
  into `Yes`/`No`/`Other` (using the repo's established `KeyCode & ~ShiftMask` idiom so a
  shifted `Y`/`N` answers too) and the model decides what that means.

## Scope (this PR)

### 1. `Tui/Screens/ExitConfirmModel.cs` — the pure model

- `Prompt` — the question text (`"Are you sure you want to exit?"`), so the screen, the help
  copy and the tests all name it once.
- `ConfirmKey { Yes, No, Other }` → `ConfirmAction { Exit, Cancel, Ignore }` via `Route`.
- **Unrecognised keys are ignored, not treated as an answer.** A yes/no whose destructive
  answer is "yes" must not exit on a stray keypress, and silently dismissing on any key would
  make a mistyped keystroke look like the app ignored the quit. The modal stays up until the
  user actually answers. (This deliberately differs from the repo's inline draft-discard
  prompts, where "anything else" means "keep editing" — there the safe answer is dismissal.)

### 2. `Tui/Screens/ExitConfirmScreen.cs` — the modal

A `Screen` (full-window, like every other modal here) with the prompt + the answer hints, and
a `Confirmed` result the host reads in its close handler. `Y`/`Enter` set `Confirmed = true`
and `Close()`; `N`/`Esc` close with `Confirmed = false`. Every keypress is marked handled so
nothing leaks to the layer beneath.

### 3. Central keybinding table + footer set

- `ScreenContext.ExitConfirm`, `KeyAction.Confirm`, and the two table entries
  (`Confirm` → `Y`, `Back` → `Esc`) so the screen's keys and its footer labels cannot drift
  (#355; `KeybindingsTests` asserts the footer against the table).
- `HelpItemSets.ExitConfirm` = `Y/↩ yes, exit` · `Esc/N no, stay`. Dispatch stays hand-rolled
  in the screen (like every screen except the main list — the migration is #398) because the
  bare `Y`/`N` answers must tolerate `Shift`, which exact-`KeyCode` dispatch does not.
- No `F1` on this set: a two-key yes/no shouldn't stack Help over itself.

### 4. Both hosts' `RequestExit()`

```
RequestExit() → already confirming? no-op : ShowScreen(new ExitConfirmScreen(),
                onClosed: () => { if (screen.Confirmed) Application.RequestStop(); })
```

- `TodoApp`: mounts over the list root (`Esc`, `Ctrl+Q`, `Ctrl+C`). Cancel restores the list
  with the cursor untouched — `CloseScreen` never rebuilds the `ListView`.
- `SingleTaskApp`: mounts over the launch-task detail root. Cancel restores the detail on the
  tab it was on. The detail's `Closed` event is what routes here, so the detail itself is
  never torn down by a cancelled exit.
- **`Ctrl+B` in single-task mode stays unconfirmed**: "open in the browser and close this tab"
  is an explicit, unambiguous action, not the ambiguous `Esc` #290 called out. Documented at
  the call site.

### 5. Help / docs copy

The `F1` help screen and the README shortcut table say the quit keys confirm first.

## Tests

- **Unit (`ExitConfirmModelTests`):** `Yes → Exit`, `No → Cancel`, `Other → Ignore`, and the
  prompt text is a question naming the exit (guards accidental copy drift into something
  ambiguous).
- **Unit (`HelpLineTests` / `KeybindingsTests`):** the new footer set renders
  `Y/↩ yes, exit · Esc/N no, stay`, is non-empty, and every `ExitConfirm` table binding is
  advertised on it under a parseable key (the existing theories pick the new context up once
  it is mapped).
- **TUI (`tui-validate`, not CI-unit-testable):** a new `exit_confirm_check.py` drives both
  roots under the PTY — dashboard: `Esc` → prompt (process alive) → `N` → list back → `Esc` →
  `Y` → process exits; single-task (`E2E_SINGLE_TASK`): `Esc` → prompt → `Esc` → detail back →
  `Esc` → `Y` → process exits.
- **Updated `single_task_launch_check.py`:** its "Esc quits the tab" leg becomes "Esc asks,
  then `Y` quits" — the intended contract change from this issue, not a weakened assertion.

## Out of scope

- A "don't ask again" setting / `--yes` flag: the issue asks for a consistent guard, and a
  preference to skip it is a separate decision (no `config.json` surface is added here).
- Confirming anything other than exit-from-root (screens' own `Esc` = cancel/back is
  unchanged), and any change to `NavigationHistory` — per the ADR the modal is never a history
  entry.
- Migrating this screen's dispatch onto `KeybindingDispatcher` (#398 covers the remaining
  screens).
