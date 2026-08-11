# Checklists (G, cont.): reorder / reparent checklist items — #569

The deferred half of **G (#460)**, part of the Task Checklists epic **#453**. The
per-item **assignee** half shipped separately (PR #568, `checklists-item-assignee.md`);
this note covers **reorder / reparent**: move a checklist item up/down within its
sibling list and indent/outdent it under a sibling, via the `orderindex` and `parent`
fields on the existing `PUT /checklist/{checklist_id}/checklist_item/{checklist_item_id}`
endpoint.

## Acceptance criteria (from #569)

- Items move up/down and indent/outdent; the new order persists and survives a task re-open.
- Illegal moves are rejected client-side with a flash and **no request**.
- A failed reorder reverts to the **exact** prior state.
- The move chords don't collide with `Ctrl+←`/`Ctrl+→` tab cycling and aren't swallowed by
  `NavSafeTabs`; a move at a group boundary is a no-op and never switches tabs.
- New chords registered in `Keybindings.cs` and both `HelpLine` sets, `#355` cross-check green.
- Unit tests for the pure order/parent computation and the legality rules.
- `dotnet test` green **first**, then `tui-validate` extends `checklist_check.py`;
  `tab_boundary_check.py` stays green.

## Design

### Chords (`Alt+↑ / ↓ / ←/→`)

`Alt+↑`/`Alt+↓` move the highlighted item up/down among its siblings; `Alt+←`/`Alt+→`
outdent/indent it. Chosen because they read as movement and sit clear of the Detail
screen's existing arrow gestures:

- `Ctrl+←`/`Ctrl+→` = tab cycle (`DetailTabNav`) — a different modifier.
- Bare `↑`/`↓` = pane scroll / tree selection (`TaskDetailScreen` claims these in `OnKey`,
  and that block already excludes `key.IsAlt`, so `Alt`+arrows fall through to a new
  checklist-tab handler placed above it).
- `NavSafeTabs` only neutralises the bare `Command.Up/Down/Left/Right`; `Alt`+arrows are
  distinct key codes and are consumed by `OnKey` before they could bubble to the tab
  control, so a boundary move is a consumed no-op, never a tab switch.

The handler is guarded on the Checklists `ListView` being front-most (like the existing
`Space`/`F7`/`F8`/`F9` blocks). A move on a header/empty-state row, or an illegal move
(first-item indent, top-item up, last-item down, root-item outdent) flashes and issues no
request.

### Pure computation — `Services/ChecklistMove.cs`

A Terminal.Gui-free companion to `ChecklistArranger`, unit-tested in isolation. It
reconstructs the same effective-parent / sibling-order view the arranger renders (ParentId
pointer wins, else structural `children`, sorted by `orderindex` then ordinal id), then for
a `(checklistId, itemId, ChecklistMoveKind)` computes either **illegal** (`null`) or a
`ChecklistMovePlan(NewParentId, NewOrderIndex, ClearParent)` — the single write to send:

- **Up / Down** — swap position with the adjacent sibling; parent unchanged. First-item Up /
  last-item Down → illegal.
- **Indent** — reparent under the **preceding sibling** (appended as its last child).
  First-item indent → illegal (no preceding sibling).
- **Outdent** — reparent under the **grandparent** (placed just after the former parent).
  Root-item outdent → illegal.
- A shared `IsLegalReparent` guard also rejects reparenting an item under **itself or its own
  descendant** (unreachable from the four gestures, but pinned by a direct unit test per the
  issue's explicit illegal-case list).

`NewOrderIndex` is a fractional index computed from the destination neighbours' current
`orderindex` (midpoint between the before/after neighbour; `±1` at an end; `0` into an empty
child list), so writing only the moved item's `orderindex` lands it in the target slot under
the arranger's `(orderindex, id)` sort. Verified in tests by re-projecting the moved tree
through `ChecklistArranger` and asserting the resulting display order/indentation.

### Optimistic transform — `ChecklistItemEdits.Move`

Mirrors `SetName`: returns a copy of the tree with the moved item's `OrderIndex` and
`ParentId` updated in place (recursing so a nested match updates consistently). The arranger
re-projects from the new `ParentId`/`OrderIndex`, so the row moves immediately. The screen
snapshots the whole `_task.Checklists` and reverts to it on failure (exact prior state), the
same `_pendingChecklistEdit` discipline the delete/rename flows use; on success the
server-confirmed checklist reconciles.

### Facade — `ClickUpClient.MoveChecklistItemAsync`

`MoveChecklistItemAsync(taskId, checklistId, itemId, string? parentId, double orderIndex,
bool clearParent)` mirrors `SetChecklistItemResolvedAsync`: PUT
`UpdateChecklistItemRequest { Orderindex = orderIndex }`, set `Parent` when reparenting under
an item, or force an explicit `"parent": null` via the additional-data bag when outdenting to
top level (the `SetTaskPriorityAsync` clear pattern). Returns the server-confirmed
`TaskChecklist` via `MapChecklist`, records the `ChecklistFields` change-marker nudge (#519).
`IClickUpClient` gets a default-throwing decl; `TaskService` a thin passthrough.

### Spec + regen

Add `parent` (string) and `orderindex` (number) to `UpdateChecklistItemRequest` in the
curated `clickup-openapi.json`, then regenerate the Kiota client
(`dotnet kiota generate …`, the exact args in `scripts/regen-client.ps1`). No hand edits to
`Generated/`. This is the one file (`UpdateChecklistItemRequest`) that overlaps the open
assignee PR #568 — additive on both sides; whichever merges second re-runs the regen.

## Phases

1. **Spec + regen + facade + model** — spec fields, regen, `MoveChecklistItemAsync`,
   interface + service passthrough; offline capturing-handler write tests (set-parent,
   clear-parent, orderindex shape).
2. **Pure computation** — `ChecklistMove` + `ChecklistMoveTests` (each gesture, every
   legality rule, arranger round-trip).
3. **Optimistic transform** — `ChecklistItemEdits.Move` + tests.
4. **TUI** — `Alt`+arrow handler, `Keybindings` table entries, both `HelpLine` Detail sets,
   `#355` cross-check green.
5. **tui-validate** — extend `checklist_check.py` (move + indent, asserting rendered
   order/indent); `tab_boundary_check.py` regression.

## Non-goals / deferred

- Group (checklist) reorder — a separate `position` write; out of scope here (#460 note).
- Resolving a bare assignee id to a name — unrelated slice.
