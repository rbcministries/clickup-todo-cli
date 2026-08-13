# Plan — Contextual chords (H): F2 = rename the highlighted task in the main list (#545)

Slice **H** of the contextual key/chord remapping epic (#537), implemented against the model recorded
in slice **A** (`docs/plans/contextual-chord-model.md`). Depends on **B** (#539, `F2` freed — Settings
→ `F10`, merged) and **E**'s facade (`SetTaskNameAsync`, #592, merged).

## Scope of *this* PR: the main task list

#545 asks for inline `F2` rename from **both** the main task list and the **Task Tree tab**. This PR
ships the **main-list** half — the decision-free, self-contained slice the model note calls out:

> *"H … extends the sub-context idea to `ScreenContext.MainList`; … add a `MainList` `F2 → RenameTask`
> binding directly, since the list has no ambiguous tabs."* — `contextual-chord-model.md` §5-H

The main list has no tabs, so there is no `F2`/`Ctrl+E` aliasing question — the same open product
decision (title-rename overlay vs. `Ctrl+E` alias on the non-item tabs) that still gates **E**'s
Task-Detail rename UI (#542) does **not** touch the list. This slice can therefore land cleanly today.

### The Task Tree tab is deferred (tracked on #545)

`F2` rename on the **Task Tree tab** lives inside `TaskDetailScreen` and is entangled with E/#542's
not-yet-landed Detail rename overlay (and the tree's node-targeting / read-only-row nuance). It is
**deferred** to land with, or after, E's Detail rename UI so the two share one overlay rather than
building a second. #545 stays **open** as its tracker; this PR does not `Closes #545`.

## What this slice does

1. **Keybinding (`Keybindings.cs`):** a new `KeyAction.RenameTask` bound `[(MainList, RenameTask)] = "F2"`,
   plus a `ScreenContext.RenameTask` editor context binding only `Help`/`Back` (Save is Enter, handled
   in-screen — a per-form focus key, like New Task / the description editor).
2. **Overlay (`RenameTaskScreen`):** a single-line title field modelled on `QuickOpenScreen` — a full
   `Screen` swapped in via `ShowScreen` (no nested `Application.Run`, honouring the single-`ListView`
   input model, #3/#38). Pre-filled with the current title; Enter saves, Esc cancels, F1 opens Help.
   Its submit decision is the pure `RenameTaskModel.Classify` (blank → stay open; unchanged → no-op
   dismiss; edited → `Result` carries the trimmed new title), so the validation seam is unit-testable.
3. **Wiring (`TodoApp`):** `.On(KeyAction.RenameTask, RenameCurrentTask)` on the list dispatcher.
   `RenameCurrentTask` guards the read-only rows — a header/spacer (no `CurrentTask()`), a foreign
   subtask (`_foreignSubtasks`, #70), and a context parent (`_contextParents`, #46) are **inert but
   flashed** — then opens the overlay. On a confirmed submit, `ApplyRename` does an optimistic
   `UpdateTaskRow(task with { Name = … }, wholesale: true)` + `Flash`, an off-thread
   `TaskService.SetTaskNameAsync`, confirm-from-server on success, revert-the-row on failure, and a
   per-field commit generation (`_nameCommitGen`) that drops a superseded out-of-order continuation —
   exactly the shape `ApplyStatus`/`ApplyPriority` use. `wholesale: true` is required because
   `ApplyFieldChanges` folds only status/priority/assignees, not `Name`, so the snapshot would flicker
   the old title back on a re-render otherwise.
4. **Footer / help / docs:** an `F2 ✏ rename` item on the `MainList` footer (clickable, re-raises F2),
   a `RenameTask` overlay footer set, an `F2` line in the F1 `HelpScreen`, and the README task-list
   shortcut table.

## No window-title change on the list

The main list's window title is workspace-based and static (`AppBranding.WindowTitle`), not
task-derived — so a rename only needs the **row text** refreshed (which `UpdateTaskRow` does in place).
The `#418/#425` terminal-title retitling is single-task-mode only and untouched here.

## Tests

- **Unit (`KeybindingsTests`):** `Settings_IsF10_OnMainList_AndF2_IsRenameTaskOnly` replaces the old
  "F2 is unbound" pin (updated, not weakened, to the stronger invariant: every `F2` binding is
  `RenameTask`); the `#355` cross-checks (`Footer_ShowsTheTableKey_ForEveryBinding`, `EveryToken_IsParseable`,
  `AllBindingsOfAnAction_ShareOneKey`) pick up the new bindings automatically once `FooterFor` maps the
  `RenameTask` context.
- **Unit (`HelpLineTests`):** the pinned `MainList` footer string updated; `MainList` carries a
  clickable `F2 ✏ rename`; the `RenameTask` set renders `↩ save · F1 ℹ · Esc cancel`; the new action
  item is in the parseable-key theory.
- **Unit (`RenameTaskModelTests`):** blank/whitespace/null → Blank; equal (incl. whitespace-only diff)
  → Unchanged; edited → Rename, trimmed; case-only change → Rename.
- **E2E (`tui-validate`):** a `rename_check.py` driving `F2` on a list row, typing a new title, and
  asserting the row reflects it — with a mutable `Name` applier added to `FakeClickUp` so the PUT echoes
  the server-confirmed title back.

## Hard-rules compliance

- **No `Generated/` hand-edits, no spec change, no Kiota regen** — reuses the merged `SetTaskNameAsync`
  facade; no new ClickUp surface.
- **ClickUp auth quirk untouched.**
- **Single sectioned `ListView` model intact** — the overlay is a transient modal `Screen` (like
  Settings / New Task / Quick Open), **not** a second focusable pane (#3), and `F2` is a function key,
  so the bare-letter type-ahead reservation (#12) is untouched.
- **No test weakened or deleted** — the one changed test is the `F2`-freed pin, tightened to the real
  invariant now that F2 is bound.
