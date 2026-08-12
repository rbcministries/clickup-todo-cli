# Plan — Checklists (G, #460): per-item assignee (shared SelectorView)

Slice **G** of the Task Checklists epic (#453), depends on **F** (#459, group CRUD — merged via PR #558).
#460 has two independently-shippable halves; per the issue's own note ("assignee first is the more
valuable of the two, and reorder can be dropped or deferred without blocking anything else in the epic")
**this plan/PR scopes to the per-item _assignee_ half only.** The **reorder / reparent** half — `orderindex`
/ `parent` moves, the `Alt+↑/↓/←/→` movement chords, the `NavSafeTabs` guard interplay (#452), and the pure
ordering/legality arranger — is **deferred to a tracked follow-up** (#569).

> **Scope note (post-review).** A standalone `F11` picker was rejected: `F11` is Windows Terminal's
> fullscreen toggle (an `Alt+Enter` alias), and — more importantly — the **#538 decision** records that in
> Task Detail **`F2` renames the highlighted item** and native modals are accepted, so the per-item assignee
> belongs **inside the checklist-item rename modal** (`F2`/`Ctrl+E`, migrating under #537/#541), not a
> separate chord. This PR therefore lands only the **UI-agnostic core** — the facade write + pure optimistic
> transform + their unit tests — and **stubs the invocation hook** at
> `TaskDetailScreen.RenameSelectedChecklistItem`. Building the assignee control into the rename modal (and
> threading the write delegate + member pool from the hosts) is tracked as **#572**.

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

### Phase 2 — UI (deferred to #572, per the #538 decision)

The invocation UI is **not** in this PR. Per the scope note above, the per-item assignee is set from the
**checklist-item rename modal** (`F2`/`Ctrl+E`, migrating under #537/#541), reusing a shared
`AssigneeSelectorView` specialisation over the frequency-ranked member pool, with the write going through the
Phase-1 facade + `ChecklistItemEdits.SetAssignee` (optimistic apply → reconcile/revert). The hosts
(`TodoApp`/`SingleTaskApp`) thread the write delegate + candidate pool there. This PR **stubs the hook** with a
doc comment at `TaskDetailScreen.RenameSelectedChecklistItem` pointing at the landed facade/transform and #572.

A standalone `F11` chord was **rejected** (Windows Terminal fullscreen alias; and #538 puts item-editing in the
rename modal), so no keybinding/footer/overlay ships here.

### Phase 3 — E2E + finalize (with #572)

The `checklist_check.py` assign leg and the `ChecklistsScenario` `PUT {assignee}` handler (echo a
`{id, username}` user object from `FakeClickUp.Members`) land with the modal in #572 — there is no live chord to
drive until then. This PR is validated by the Phase-1 unit tests (facade set/clear + transform) plus the
unchanged `checklist_check.py` suite (8 legs) staying green.

## Acceptance criteria

**This PR (core):**

- [x] Facade `SetChecklistItemAssigneeAsync` sets (user id) and clears (`null` → explicit `"assignee": null`),
      pinned by unit tests asserting the outgoing JSON body (number vs JSON null).
- [x] Pure `ChecklistItemEdits.SetAssignee` transform (set / clear / nested / no-op), unit-tested.
- [x] Spec-driven `assignee` field via Kiota regen (no `Generated/` hand edits); `dotnet test` green; the
      unchanged `checklist_check.py` suite stays green.

**Deferred to #572 (the rename-modal UI):**

- [ ] An item's assignee can be set and cleared from the rename modal, persists in ClickUp, and renders on the
      row — via a shared `SelectorView` (#243) specialisation, no fork.
- [ ] A failed assignee write reverts to the exact prior state.
- [ ] No new bare-letter chord; the #355 footer/keybinding cross-checks stay green; `tui-validate` covers it.

## Deferred to a tracked follow-up (reorder / reparent half of #460)

Move up/down + indent/outdent via `orderindex`/`parent` on the same `PUT`; the pure ordering + legality rules in
B's arranger (illegal: indent the first item in a group, reparent under a descendant); the `Alt+↑/↓/←/→`
movement chords and their `NavSafeTabs` nav-guard interplay (#452); snapshot-the-sibling-list revert. Tracked in
**#569** so #460's remaining half is not lost.

## Follow-ups

- **#572** — build the per-item assignee control into the checklist-item rename modal (`F2`/`Ctrl+E`, per the
  #538 decision), consuming this PR's landed facade + transform.
- **#569** — the reorder / reparent half of #460 (above).
