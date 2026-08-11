# Plan — Checklists (G, #460): per-item assignee (shared SelectorView)

Slice **G** of the Task Checklists epic (#453), depends on **F** (#459, group CRUD — merged via PR #558).
#460 has two independently-shippable halves; per the issue's own note ("assignee first is the more
valuable of the two, and reorder can be dropped or deferred without blocking anything else in the epic")
**this plan/PR scopes to the per-item _assignee_ half only.** The **reorder / reparent** half — `orderindex`
/ `parent` moves, the `Alt+↑/↓/←/→` movement chords, the `NavSafeTabs` guard interplay (#452), and the pure
ordering/legality arranger — is **deferred to a tracked follow-up** and noted in the PR.

## Background (what already exists on `main`)

The read side of a checklist-item assignee is done (C/B, #455/#456): the domain
`TaskChecklistItem.Assignee` (`TaskAssignee?`) is parsed by `ChecklistReader.ReadAssignee` (tolerating
absent/null, a bare user id, or a full user object) and the item row already renders the assignee suffix
(the `checklist_check.py` populated leg asserts `Ada Lovelace` on the one assigned item). What's missing is
the **write** path and a way to invoke it from the UI.

The item-CRUD template (E, #458) is the pattern to copy end-to-end:

- `ClickUpClient.SetChecklistItemResolvedAsync` / `RenameChecklistItemAsync` — `PUT /checklist/{id}/checklist_item/{id}`
  with a single-field body, returning the server-confirmed `TaskChecklist` via `MapChecklist`, with a `#519`
  change-marker nudge on the resolve write.
- `ChecklistItemEdits.SetName` — the pure, order-preserving optimistic transform (a value-identical no-op when
  the target is missing).
- `TaskDetailScreen.RenameChecklistItem` — the optimistic-apply → facade-call → reconcile-to-server /
  revert-on-failure discipline (`_checklistWriteInFlight` guard, `_pendingChecklistEdit` overlay fn,
  `ReplaceChecklist` reconcile, `snapshot` revert, `RequestFlash`).
- The shared selector: `SelectorView`/`SelectorModel` (#243) backing `AssigneeSelectorView`/`AssigneeSelectorModel`,
  already driven in **Quick Updates** (`QuickUpdatesScreen`) in `SelectorMode.ImmediateApply` against
  `AssigneeFrequencyCache.Match`/`TopMostFrequent`.

## The write: spec + regen (not a hand edit)

The generated `UpdateChecklistItemRequest` currently exposes only `name`/`resolved`; its own doc comment says
"Assignee/reparent land with F–G." So this is the maintainer-intended spec change:

- Add to `clickup-openapi.json` → `components.schemas.UpdateChecklistItemRequest.properties`:
  `"assignee": { "type": "integer", "format": "int64", "nullable": true }` (mirrors the nullable-int precedent
  of `UpdateTaskRequest.priority` / `CreateTaskRequest.due_date`).
- Regen with the pinned Kiota (`dotnet kiota generate … --clean-output`; `pwsh` is absent in this env but the
  restored `kiota` tool runs the same command the script wraps — a **no-op regen produces a zero diff**, so the
  change is localized to `UpdateChecklistItemRequest.cs`). Never hand-edit `Generated/`.

**Set vs clear semantics.** ClickUp sets a checklist-item assignee with the integer user id and clears it by
sending `assignee: null` (the read model documents `null` = unassigned; no other clear shape is attested). Kiota
**omits** a null typed property, so — exactly like `SetTaskPriorityAsync`'s clear — the facade sends the typed
`Assignee` for a set and forces an explicit `"assignee": null` via `request.AdditionalData["assignee"] = null!`
for a clear. Both shapes are pinned by a unit test asserting the outgoing JSON body.

## Phases

### Phase 1 — spec / client / facade / service / pure transform + unit tests (all CI-testable)

1. Spec: add the `assignee` property; regen the client (verify a localized diff).
2. `ClickUpClient.SetChecklistItemAssigneeAsync(string taskId, string checklistId, string itemId, long? assigneeId, CancellationToken)`
   — mirror `SetChecklistItemResolvedAsync` (returns the server-confirmed `TaskChecklist`, records a
   `ChecklistFields` nudge keyed by `taskId`). Set → typed `Assignee`; clear (`assigneeId is null`) →
   `AdditionalData["assignee"] = null!`.
3. `IClickUpClient` — default-throwing declaration; `TaskService` — thin passthrough (mirror the other
   checklist-item writes).
4. `ChecklistItemEdits.SetAssignee(checklists, checklistId, itemId, TaskAssignee?)` — pure optimistic transform
   mirroring `SetName` (recurse into `Children`; value-identical no-op when the checklist/item is missing).
5. Tests:
   - `ClickUpClientChecklistWriteTests` — a **set** fact (`PUT …/checklist_item/{id}`, body `assignee` is a
     number equal to the id, no `name`/`resolved`) and a **clear** fact (body `assignee` is `JsonValueKind.Null`).
   - `ChecklistItemEditsTests` — `SetAssignee` sets, clears (→ null), updates a nested item, and is a no-op on a
     missing checklist/item.

### Phase 2 — TUI wiring (build-verified; TG UI is not CI-unit-testable)

6. `TaskDetailScreen`: a new `_setChecklistItemAssigneeAsync` delegate field; an **`F11`** chord (see below),
   guarded to the checklist ListView being front-most and scoped to a non-header item row, that opens a
   bottom-anchored modal hosting an `AssigneeSelectorView` in `ImmediateApply` mode. Single-assignee semantics
   are enforced **through** the shared multi-select view (no fork, per the acceptance criterion) by the
   `applyAsync` handler returning the authoritative single-element (or empty) selection: selecting a member
   writes `assignee=id` and returns `[member]` (so the view unticks any prior pick); unticking writes
   `assignee=null` and returns `[]`. The handler also applies `ChecklistItemEdits.SetAssignee` optimistically to
   the screen's checklist rows and reconciles to / reverts from server truth, reusing the `RenameChecklistItem`
   discipline.
7. Host wiring: `TodoApp` + `SingleTaskApp` lambdas → `TaskService` → `ClickUpClient`.
8. `Keybindings`: a new `AssignChecklistItem` action, `(Detail) = "F11"`. `HelpLine`: an `F11  👤 assign`
   action item in **both** the `Detail` and `DetailWithTaskTree` footer sets (kept in sync with the table per the
   #355 `KeybindingsTests`/`HelpLineTests` cross-check).

**Chord choice — `F11`.** `F7/F8/F9` (checklist add/rename/delete) form the checklist function-key cluster;
`Space` toggles, `Ctrl+G` creates a group. `F11` is unused anywhere in the app, so it adds a checklist-tab
action with **zero** cross-context collision and no entanglement with the #537 contextual-chord epic (which is
eyeing `F2` for rename) — deliberately avoiding a contextual overload of `Ctrl+U` (task Quick Updates), which is
exactly the kind of decision #538 owns. Open to the maintainer's preference.

### Phase 3 — E2E + finalize

9. Extend `checklist_check.py` with an assign leg: on the populated task, select an unassigned item, `F11`, pick a
   member from the selector, assert the assignee suffix renders on the row and survives a `Ctrl+R` refresh
   (the fake backend persists the write). Requires the E2E fake backend to (a) serve workspace members for the
   selector pool and (b) round-trip the `PUT` `assignee` on the checklist item. If that backend surface proves
   larger than a one-session slice, the E2E leg is deferred to the same reorder follow-up and called out in the
   PR (the write + transform stay fully unit-tested regardless).
10. `dotnet build/test` green, `dotnet format`, then `tui-validate`; first-pass review subagent; ready-for-review.

## Acceptance criteria (assignee half of #460)

- [ ] An item's assignee can be set and cleared, persists in ClickUp, and renders on the row.
- [ ] The assignee picker is a specialisation of the shared `SelectorView` (#243) — no duplicated selector.
- [ ] A failed assignee write reverts to the exact prior state.
- [ ] The new chord doesn't collide with `Ctrl+←`/`Ctrl+→` tab cycling and is registered in `Keybindings` +
      both `HelpLine` sets, with the #355 cross-check tests green.
- [ ] Unit tests for the pure transform and the facade write body (set + clear); `dotnet test` green, then
      `tui-validate`.

## Deferred to a tracked follow-up (reorder / reparent half of #460)

Move up/down + indent/outdent via `orderindex`/`parent` on the same `PUT`; the pure ordering + legality rules in
B's arranger (illegal: indent the first item in a group, reparent under a descendant); the `Alt+↑/↓/←/→`
movement chords and their `NavSafeTabs` nav-guard interplay (#452); snapshot-the-sibling-list revert. Tracked so
#460's remaining half is not lost.
