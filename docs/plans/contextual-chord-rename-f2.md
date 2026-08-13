# Plan — Contextual chords (D): `F2` = rename the highlighted checklist item; retire `F7`/`F8`/`F9` (#541)

Slice **D** of the contextual key/chord remapping epic (#537), implemented against the model in
slice **A** (`docs/plans/contextual-chord-model.md`, §5-D). Dependencies all landed on `main`:

- **B (#539)** freed `F2` (Settings → `F10`).
- **C (#540 / PR #586)** retargeted `AddChecklistItem` `F7 → Ctrl+N` and introduced the
  `DetailSubContext` + `ResolveDetail` activation layer — `F7` is unbound.
- **F (#543 / PR #595)** retargeted `DeleteChecklistItem` `F9 → Delete` behind a confirmation —
  `F9` is unbound.

After this slice **no `F7`/`F8`/`F9` binding remains anywhere in the app** — the last surviving
#458 stopgap (`F8` = rename) moves to the conventional `F2`.

## Scope (and what is deliberately *not* in it)

Per #541's body — "Reuse the existing rename overlay + optimistic `SetName` + reconcile logic from
#458 **unchanged; only the trigger key changes**" — this slice changes **only the trigger key**:

- **`Keybindings` table:** retarget `(Detail, RenameChecklistItem)` `F8 → F2`. No collision — `F2` is
  otherwise bound only to `RenameTask` in `MainList` (slice H, #545); the two live in different
  `ScreenContext`s, and `RenameChecklistItem` is the only `F2`-bound action in any `Detail`
  sub-context, so the §2.2 anti-collision invariant holds.
- **Dispatch (`TaskDetailScreen.OnKey`):** the checklist rename branch fires on `F2` instead of `F8`,
  routed through `Keybindings.ResolveDetail(sub, "F2") == RenameChecklistItem` exactly like the C
  (`Ctrl+N`) and F (`Delete`) branches, so dispatch and the per-tab footer label can't drift. The
  row-kind branch (header → group, item → item) is unchanged.
- **Footer:** `Detail` / `DetailWithTaskTree` sets change `F8 ✏ rename → F2 ✏ rename`.
- **Tests:** a named pin `RenameChecklistItem_IsF2_AndNoBindingUsesF8` (sibling to the `F7`/`F9`
  pins); the `Settings_IsF10…` guard's "every `F2` is `RenameTask`" clause is **updated, not
  weakened**, to the real convention it stood in for (every `F2` is a *rename* action); and the
  `tui-validate` `checklist_check.py` rename legs drive `F2`.

**Not in scope (deferred):** the model note (§3, §5-D) floats relabeling the footer to **Edit** and
renaming the action `RenameChecklistItem → EditChecklistItem`, because #572 puts an assignee control
in the same modal. Both are explicitly **cosmetic / "consider"** in the model, and **#572 itself
shipped keeping the `RenameChecklistItem` name and the "Rename item" overlay title** — so the honest,
consistent move is to keep them: the overlay is still titled *Rename item* / *Rename checklist*, and a
`✏ rename` footer hint that matches that title is correct. Relabeling only the footer to "Edit" while
the overlay says "Rename" would introduce drift, not remove it. If the maintainer wants the "Edit"
framing, it's a clean cosmetic follow-up on #572's surface, not part of this key-rebind.

## Acceptance criteria (from #541)

- [x] `F2` renames the highlighted checklist item (item row → item; header row → group);
  `F7`/`F8`/`F9` are bound to nothing anywhere (pinned by unit test).
- [x] Footer / `Keybindings.cs` / #355 cross-check updated and green.
- [x] `checklist_check.py` CRUD + group-rename + assignee legs drive `F2`; `description_edit_check.py`
  / `detail_comment_check.py` unaffected; no second focusable pane / latency regression (#3).

## Invariants honored

- **#12 type-ahead:** `F2` is a function key — no bare letter claimed.
- **#3/#38 single focusable `ListView`:** pure table-lookup on the existing keypress path; no new
  pane, no run-loop, no per-keypress allocation beyond today's `OnKey`.
- **#355 anti-drift:** the token is single-sourced in `Keybindings.Map`; dispatch and footer both read
  `ResolveDetail`/`DetailBindings`, cross-checked by `KeybindingsTests`.
