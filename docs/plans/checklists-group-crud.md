# Plan — Checklists (F, #459): checklist group CRUD — create, rename, delete

Slice **F** of the Task Checklists epic (#453). Depends on **E** (#458, item CRUD) — merged via PR #535 —
so the `ChecklistItemEdits` pure-transform template, the `ClickUpClient` checklist-write facade shape, the
optimistic-update / revert-on-failure / flash discipline, the F7/F8/F9 chord + name-overlay + inline
Y/N-confirm patterns, and the E2E fake-backend + `checklist_check.py` harness are all on `main` to build on.
Out of scope: per-item assignee and item reorder/reparent (**G**, #460); reordering *groups* (the optional
`PUT /checklist/{id}` `position` field — folded into **G** since group reorder is not needed here).

## The chord decision (recorded here before implementation, mirroring E's decision discipline)

Items and groups share the Checklists tab; E already owns `F7` (add item), `F8` (rename item), `F9`
(delete item), all guarded to the Checklists `ListView` being front-most. F must add **group** create /
rename / delete **without breaking E** and **without new interaction vocabulary** (no new overlay style, no
second focusable pane).

**Decision:**

- **`F8` = Rename**, row-kind-scoped: on a **checklist-header** row → rename the **group**; on an **item** row
  → rename the **item** (E, unchanged). No new key; `F8` stays bound to `RenameChecklistItem` in the table.
- **`F9` = Delete**, row-kind-scoped: on a **header** row → delete the **group** (destructive confirm naming
  the group and its item count); on an **item** row → delete the **item** (E, unchanged). No new key.
- **`Ctrl+G` = New checklist (group)** — a new `KeyAction.NewChecklist` bound in `ScreenContext.Detail`,
  available whenever the Checklists tab is front-most **including on the empty-state row** (so "create even
  when the task has none, from the empty-state row" is satisfied). Opens the same single-line name overlay E
  built (`ShowChecklistItemEditor`), extended with a group-create kind.

**Rationale for the split (why not reuse `F7` for group-create):**

1. **E's `F7`-on-a-header is load-bearing and must stay add-item.** `checklist_check.py::run_crud` presses
   `F7` while the *Release steps header* is selected and asserts an **item** is added to that group
   (`Release steps  (1/4)`), and adding the first item to a freshly-created empty group is likewise
   `F7`-on-its-header. Repurposing `F7`-on-header to "create group" would break that shipped test and make an
   empty group un-fillable. Never weaken a test to fit a new gesture — so `F7` keeps E's exact meaning.
2. **`F8`/`F9`-on-a-header are free.** E only ever drives `F8`/`F9` from *item* rows (a header flashes
   "headers can't be renamed/deleted" today). Overloading them by row kind adds group rename/delete with
   **zero new keys** and no risk to E's tests.
3. **`Ctrl+G` fits the established vocabulary.** Bare letters are reserved for the ListView type-ahead (#12),
   so create needs a chord; Task Detail already uses `Ctrl`-letter command chords (`Ctrl+N/E/T/A/B/U`), so a
   `Ctrl`-chord is not a new *style*. `G` = checklist **G**roup. `Ctrl+G` (input byte `0x07`) is an ordinary
   `Ctrl`+letter to the driver on input (the "bell" is an output-only concern) and is unbound in Detail and
   globally. It is guarded to the Checklists tab exactly like `Space`/`F7`–`F9`, so it is inert elsewhere.
4. **Destructive delete is confirmed with the count.** `F9` on a header arms the same inline `(Y / N)` confirm
   E uses (answered by `Enter`/`Esc` in `OnKey`, since a bare `Y` is eaten by the ListView type-ahead), but
   the prompt names the group and its item count — e.g. `Delete checklist 'Release steps' and its 3 items? (Enter / Esc)`.

## API + curated spec + regen

ClickUp group endpoints (added to `src/ClickUpTodo/ClickUp/clickup-openapi.json`, then
`dotnet tool restore` + `pwsh scripts/regen-client.ps1`; **never hand-edit `ClickUp/Generated/`**):

- **Create** — `POST /v2/task/{task_id}/checklist` with `{ "name": "…" }`. ClickUp echoes the new checklist as
  `{ "checklist": { … } }` (the same envelope as the item writes), so create returns the server-reconciled
  group (real id/orderindex, empty items). New builder: `V2.Task[taskId].Checklist.PostAsync`.
- **Rename** — `PUT /v2/checklist/{checklist_id}` with `{ "name": "…" }`. Same `{ checklist }` echo. New
  builder: `V2.Checklist[checklistId].PutAsync`.
- **Delete** — `DELETE /v2/checklist/{checklist_id}`. Empty body (like `DELETE .../checklist_item/{id}`), so
  the facade returns `Task` (void) and the optimistic local removal stands. New builder:
  `V2.Checklist[checklistId].DeleteAsync`.

Spec additions:
- New path item `"/v2/task/{task_id}/checklist"` with `post` → `$ref CreateChecklistRequest`, response
  `$ref ChecklistResponse`.
- New path item `"/v2/checklist/{checklist_id}"` with `put` (`$ref UpdateChecklistRequest`, response
  `$ref ChecklistResponse`) and `delete` (empty 200, no content).
- Schemas: `CreateChecklistRequest { name (required) }`, `UpdateChecklistRequest { name }` (name-only;
  `position`/group reorder deferred to **G**), `ChecklistResponse { checklist: $ref Checklist }` (the
  `{ checklist }` envelope for the group create/rename responses; distinct name from `ChecklistItemResponse`
  for readability, identical shape).

Regen is byte-verified by a no-op run producing zero diff (per the E plan; `pwsh` may be unavailable but the
`dotnet kiota generate` command in `regen-client.ps1` is identical and byte-faithful).

## Facade (`IClickUpClient` + `ClickUpClient`) + service (`TaskService`)

Three methods, each mirroring the E item writes (default-throwing on the interface so read-only fakes need
not implement them; `Guard(...)` in the class; generated types never escape; `{ checklist }` unwrapped via
the existing `MapChecklist`):

- `Task<TaskChecklist> CreateChecklistAsync(string taskId, string name, CancellationToken ct)`
  → `V2.Task[taskId].Checklist.PostAsync(new CreateChecklistRequest { Name = name })` → unwrap → `MapChecklist`.
- `Task<TaskChecklist> RenameChecklistAsync(string checklistId, string name, CancellationToken ct)`
  → `V2.Checklist[checklistId].PutAsync(new UpdateChecklistRequest { Name = name })` → unwrap → `MapChecklist`.
- `Task DeleteChecklistAsync(string checklistId, CancellationToken ct)`
  → `using var _ = await V2.Checklist[checklistId].DeleteAsync(...)` (void, mirroring `DeleteChecklistItemAsync`).

No change-marker nudge (matching E's create/rename/delete-item, which don't nudge — only the D toggle does).
`TaskService` gets three thin passthroughs so the screen depends only on `TaskService`.

## Pure transforms (CI-testable) — `ChecklistGroupEdits`

A new `ChecklistItemEdits`-style static class (immutable, order-preserving, no Terminal.Gui):

- `InsertProvisional(checklists, provisional)` → the list with a new (empty) group appended — the optimistic
  pre-request row for a create. `ProvisionalChecklistId` is the shared sentinel id.
- `Rename(checklists, checklistId, name)` → the list with the matching group's `Name` changed; every other
  group carried through unchanged. No-op on a miss.
- `Remove(checklists, checklistId)` → the list with the matching group (and, by construction, all its items)
  removed. No-op on a miss.
- Name normalization reuses `ChecklistItemEdits.NormalizeName` (already public — trims, rejects empty/whitespace).
- (No `NewChecklistId` before→after diff, unlike the item level's `NewItemId`: a group-create response *is*
  the new checklist, so the screen selects it by `server.Id` directly.)

Post-delete **selection** — a new sibling to `ChecklistTabModel.SelectAfterDelete`:

- `SelectAfterGroupDelete(oldRows, deletedHeaderIndex, newRows)` → prefer the **next group header**, else the
  **previous group header**, else index `0` (the empty-state row when the last group is gone). Pure, unit-tested.
- `DeleteGroupPrompt(name, itemCount)` → the destructive confirm string, e.g.
  `Delete checklist 'X' and its 3 items? (Enter / Esc)`, `… and its 1 item?`, or `Delete checklist 'X'? (Enter / Esc)`
  for an empty group. Pure, unit-tested.

## TUI wiring (`TaskDetailScreen`, build + `tui-validate` only)

- **`Ctrl+G`** → new `KeyAction.NewChecklist`, bound in `Keybindings.cs` under `ScreenContext.Detail` and
  shown in **both** `HelpItemSets.Detail` + `DetailWithTaskTree` (per #355; the `KeybindingsTests` cross-check
  asserts every Detail binding's token appears on the footer). Dispatched in `OnKey` guarded to
  `ReferenceEquals(_tabs.Value, _checklistList)`, alongside the existing `Space`/`F7`–`F9` block. Opens the
  E name overlay in a new `ChecklistItemEditKind.NewGroup` mode (empty field, title "New checklist").
- **`F8`/`F9`** gain a row-kind branch in `OnKey` (before E's item path): when the selected row `IsHeader`,
  route to `RenameSelectedChecklistGroup` / `DeleteSelectedChecklistGroup`; otherwise E's item path (unchanged).
  Footer labels broaden to kind-neutral: `F8 "✏ rename"`, `F9 "🗑 delete"`; `F7` stays `"➕ item"`; add
  `Ctrl+G "➕ list"`.
- **Group name overlay** reuses `ShowChecklistItemEditor` / `SubmitChecklistItemEditor` extended for the
  `NewGroup` and `RenameGroup` kinds (rename pre-fills the group name and keeps E's dirty/discard-confirm).
- **Delete-group confirm** reuses the `_checklistDeletePending` inline-armed `(Y / N)` mechanism, but carries a
  `ChecklistGroupDeletePending` (checklist id + name + item count) answered in `OnKey` with the count-bearing
  prompt; `Enter` deletes, `Esc` cancels; leaving/re-entering a tab clears it (as E does).
- **Optimistic + revert**, mirroring E's item CRUD, reusing `_checklistWriteInFlight` / `_pendingChecklistEdit`
  / the pending-overlay-in-`UpdateData` discipline so a 30 s auto-refresh or `F5` mid-write neither clobbers
  nor double-applies the edit:
  - *Create*: insert a provisional empty group (sentinel id) via `ChecklistGroupEdits.InsertProvisional`,
    re-project, select its header; fire `createChecklistAsync`; on success reconcile idempotently by id
    (drop the provisional **and** any already-present copy of `server.Id`, then append the server group once
    — so a refresh landing mid-write can't leave a duplicate header) and select it by `server.Id`; on failure
    remove the provisional + flash.
  - *Rename*: `ChecklistGroupEdits.Rename` optimistically (selection stays by id), fire `renameChecklistAsync`,
    on success replace with the server group, on failure revert to the snapshot + flash.
  - *Delete*: snapshot the whole `_task.Checklists`; compute `SelectAfterGroupDelete`;
    `ChecklistGroupEdits.Remove` optimistically; fire `deleteChecklistAsync`; on failure restore the snapshot
    (group **and every item**, in order, at its original position) + prior selection + flash.
- **Host wiring** — new ctor callbacks on `TaskDetailScreen` wired by both hosts, exactly like the E callbacks:
  - `createChecklistAsync: (name, ct) => _tasks.CreateChecklistAsync(taskId, name, ct)` (the host closes over
    the task id, as the D toggle does, since the POST is task-scoped);
  - `renameChecklistAsync: (checklistId, name, ct) => _tasks.RenameChecklistAsync(checklistId, name, ct)`;
  - `deleteChecklistAsync: (checklistId, ct) => _tasks.DeleteChecklistAsync(checklistId, ct)`.

## Tests

- **Unit** `ChecklistGroupEditsTests`: rename a group / no-op on a miss; remove a group (its items go too) and
  a miss; insert-provisional appends; `NewChecklistId` diff (one new / none / ambiguous).
- **Unit** `ChecklistTabModelTests` additions: `SelectAfterGroupDelete` — next header, else previous, else the
  empty state (index 0) when the last group is deleted; `DeleteGroupPrompt` — plural / singular / zero-items.
- **Facade** `ClickUpClientChecklistWriteTests` additions (`CapturingHandler` style): create asserts
  `POST …/task/{id}/checklist`, `{name}` body, mapped result; rename asserts `PUT …/checklist/{id}`, `{name}`
  body; delete asserts `HttpMethod.Delete`, correct URL, no body.
- **Facade integration** (`SkippableFact`, `CLICKUP_TOKEN`-gated): create a task, create a checklist on it, add
  an item, rename the checklist, delete the checklist, asserting each server response; clean up. Skips without
  credentials.
- **`tui-validate`** (only after `dotnet test` is green): extend `checklist_check.py` with an F leg — on a
  checklist-free task (empty-state), `Ctrl+G` create a group → the group header appears; `F7` add an item to
  it → `(0/1)`; `F9`+`Enter` on the header delete the group → the empty state (`No checklists on this task.`)
  is reached again. Confirm the existing populated/empty/toggle/CRUD/add-cancel/delete-confirm legs and
  `detail_comment_check.py` / `description_edit_check.py` still pass (the F chords don't shadow theirs). The
  `Program.cs` fake gains `POST /task/{id}/checklist`, `PUT /checklist/{id}`, `DELETE /checklist/{id}` routes;
  `RouteTableTests` stays green (non-ambiguous segment counts/methods).

## Phases

1. **Spec + regen + facade + service + `ChecklistGroupEdits` + `ChecklistTabModel` helpers + unit/facade
   tests** — CI-green, no TUI surface. Push → draft PR.
2. **TUI wiring** — `Ctrl+G` create, `F8`/`F9` header branch, the group name overlay + count-bearing delete
   confirm, optimistic + revert, keybindings + footer, host wiring; fake-backend routes + `checklist_check.py`
   F leg. Build; then `tui-validate`.

## Hard rules honored

- **No hand edits under `Generated/`** — spec edit + `dotnet kiota generate` only.
- Generated types never escape the facade; the domain carries `TaskChecklist`/`TaskChecklistItem`.
- Personal-token raw `Authorization` header untouched; the API-boundary test is a `SkippableFact`.
- Single sectioned `ListView` main-list model untouched; no second focusable pane (#3) — F rides the existing
  Checklists tab `ListView` from #456.
- One source of truth for shortcuts: `Ctrl+G` lands in `Keybindings.cs` **and** both `HelpItemSets` Detail
  sets, cross-checked by `KeybindingsTests`; `F8`/`F9` stay one table entry each (the item/group split is a
  runtime row-kind branch, not a new binding).
- Optimistic write + revert-on-failure + flash, matching the D/E precedent.
- E's shipped `F7` add-item behavior and its `checklist_check.py` legs are preserved unchanged.
