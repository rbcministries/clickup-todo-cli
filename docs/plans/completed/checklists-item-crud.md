# Plan — Checklists (E, #458): checklist item CRUD — add, rename, delete

Slice **E** of the Task Checklists epic (#453). Depends on **D** (#457, `Space`-toggle) and **A–C**
(#454 read model, #455 row projection, #456 the Checklists tab) — all merged. Out of scope: checklist
**group** create/rename/delete (**F**, #459) and per-item assignee / reorder / reparent (**G**, #460).

## The blocking decision — which chords (recorded on #458 before implementation)

**Decision: `F7` add · `F8` rename · `F9` delete** — three new `KeyAction`s bound in
`ScreenContext.Detail`, shown in both Detail footer sets, and guarded at runtime to the Checklists tab
being front-most. This is the issue's **option 2 (new, unshadowed chords)**. Rationale:

1. **No shadowing.** `Ctrl+N` (add comment) and `Ctrl+E` (edit description) act on the whole task and
   stay useful on the Checklists tab, so tab-scoped *rebinding* them (option 1) removes real function.
   It would also break the flat `(context, action) → key` table the #355 anti-drift cross-check
   (`KeybindingsTests`) depends on, and force a per-tab footer dimension the codebase deliberately does
   not have (`ScreenContext` is a flat enum; the footer varies by host/overlay, not by tab).
2. **Matches the D precedent exactly.** D added `Space`/`ToggleChecklistItem` as a `ScreenContext.Detail`
   binding, shown unconditionally in `HelpItemSets.Detail` + `DetailWithTaskTree`, and guarded in
   `TaskDetailScreen.OnKey` to `ReferenceEquals(_tabs.Value, _checklistList)`. `F7`/`F8`/`F9` follow the
   identical shape — no new `ScreenContext`, no dispatcher change, no footer tab-dimension.
3. **Bare letters are unavailable.** The Checklists tab's `ListView` uses bare-letter type-ahead (like
   the main list, #12), so `a`/`r`/`d` would collide. Function keys are the app's established *command*
   convention on the footer (F1/F5/F6), so F7/F8/F9 extend it unambiguously.
4. **Delete is confirmed.** `F9` arms an inline `(Y / N)` confirm (reusing the description editor's
   discard-confirm pattern) so an un-undoable delete is never a single keypress. The add/rename input
   overlay shows its own footer set (Enter submit / Esc cancel), mirroring the comment composer.
5. **Trade-off:** three commands now show in the Detail footer on every tab (as `Space` already does);
   `HelpLine.Fit` truncates gracefully on narrow terminals. A tab-aware footer that hides inert
   tab-specific hints is a possible follow-up, deliberately out of E's scope.

## What exists to build on

- **Read model / projection / tab** (A–C): `TaskDetail.Checklists` → `ChecklistArranger.Project` →
  `ChecklistProjection` (flat `ChecklistRow`s each carrying `ChecklistId` + `ItemId`, + aggregate
  progress); `ChecklistTabModel` (`TabTitle`/`RenderRow`/`Signature`/`AnchorSelection`). Progress is
  derived from the **projected items**, not the API `resolved`/`unresolved` counts, so any tree edit
  re-projects to correct counts for free.
- **Write seam** (D, #457): `ClickUpClient.SetChecklistItemResolvedAsync` (`PUT
  /v2/checklist/{id}/checklist_item/{id}`, unwraps `{ checklist }`, maps via `MapChecklist`);
  `IClickUpClient` declares it default-throwing; `TaskService` is a thin passthrough; the screen holds an
  injected `Func<…, Task>` callback wired by both hosts; `ToggleSelectedChecklistItem` does
  optimistic-update + off-thread write + revert-on-failure + flash, and re-applies a pending optimistic
  overlay inside `UpdateData` so a mid-write refresh neither clobbers nor double-applies it.
- **Pure transform** (D): `ChecklistToggle.SetResolved` — immutable, order-preserving, recurses into
  `Children`, no-op on a missing checklist/item. The template for E's transforms.

## API + facade

ClickUp checklist-item endpoints:
- **Create** — `POST /v2/checklist/{checklist_id}/checklist_item` with `{ "name": "…" }`. Echoes the
  whole parent checklist as `{ "checklist": { … } }` (same envelope as the D toggle) — so create returns
  the server-reconciled group, including the new item with its real id/orderindex.
- **Rename** — `PUT /v2/checklist/{checklist_id}/checklist_item/{checklist_item_id}` with `{ "name":
  "…" }` — the path **D already added**; only the request schema gains an optional `name`. Same
  `{ checklist }` echo.
- **Delete** — `DELETE /v2/checklist/{checklist_id}/checklist_item/{checklist_item_id}`. ClickUp returns
  an empty body (like `DELETE /task/{id}`), so the facade returns `Task` (void) and the optimistic local
  removal stands.

### Spec (`clickup-openapi.json`) + regen

- Add `post` to a new path item `"/v2/checklist/{checklist_id}/checklist_item"` (create), response
  `$ref ChecklistItemResponse`, body `$ref CreateChecklistItemRequest`.
- Add `delete` to the existing `"/v2/checklist/{checklist_id}/checklist_item/{checklist_item_id}"` path
  item, `responses.200` with **no content** (empty body → Kiota generates a void `DeleteAsync`).
- Extend `UpdateChecklistItemRequest` with an optional `name` (string) for rename; update its
  `description` (drop the "only resolved is modelled" note).
- Add `CreateChecklistItemRequest` `{ "name": { "type": "string" } }` (required `name`).
- Regenerate with the exact `scripts/regen-client.ps1` command via `dotnet kiota generate …`
  (`pwsh` is unavailable in this environment; the command is identical and byte-faithful — verified by a
  no-op regen producing zero diff). **Never hand-edit `Generated/`.** New builders appear:
  `Checklist_itemRequestBuilder.PostAsync`, `WithChecklist_item_ItemRequestBuilder.DeleteAsync`, and
  `UpdateChecklistItemRequest.Name`.

### Facade (`IClickUpClient` + `ClickUpClient`) + service (`TaskService`)

Three methods, each mirroring `SetChecklistItemResolvedAsync` (default-throwing on the interface,
`Guard(...)` in the class, generated types never escape):

- `Task<TaskChecklist> CreateChecklistItemAsync(string checklistId, string name, CancellationToken ct)`
  → `PostAsync` → unwrap `{ checklist }` → `MapChecklist`.
- `Task<TaskChecklist> RenameChecklistItemAsync(string checklistId, string itemId, string name, CancellationToken ct)`
  → `PutAsync(new UpdateChecklistItemRequest { Name = name })` → unwrap → `MapChecklist`.
- `Task DeleteChecklistItemAsync(string checklistId, string itemId, CancellationToken ct)`
  → `using var _ = await …DeleteAsync(...)` (void, mirroring `DeleteTaskAsync`).

`TaskService` gets three thin passthroughs so the screen depends only on `TaskService`.

## Pure transforms (CI-testable) — `ChecklistItemEdits`

A new `ChecklistToggle`-style static class, immutable + order-preserving, no Terminal.Gui:

- `SetName(checklists, checklistId, itemId, name)` → tree with exactly the matching item's `Name`
  changed (flat `Items` **and** any nested `Children`, like `SetInItems`). No-op on a missing
  checklist/item.
- `Remove(checklists, checklistId, itemId)` → tree with the item (and, by construction, its
  descendants — removing a node drops its `Children`) removed from both the flat list and any parent's
  `Children`. No-op on a miss.
- `InsertProvisional(checklists, checklistId, item)` → tree with `item` appended to the target
  checklist's top-level `Items` (a newly created item is top-level; reparent is **G**). Used for the
  optimistic pre-request row.
- `NormalizeName(raw)` → `string?`: trims; returns `null` for empty/whitespace-only (client-side reject,
  no request). Mirrors `NewTaskForm` name handling.
- `NewItemId(before, after)` → the id present in `after` but not `before` (the server-created item), for
  landing the selection on the freshly-added item after a create round-trips.

Post-mutation **selection** stays in `ChecklistTabModel` as a sibling to `AnchorSelection`:

- `SelectAfterDelete(oldRows, deletedIndex, newRows)` → the index to select after a delete: prefer the
  next **item** row in the same checklist after the deleted subtree, else the previous item row in the
  same checklist, else that checklist's header row; falls back to `AnchorSelection`'s clamp when nothing
  matches. Pure, unit-tested. (Add/rename keep the cursor via the existing `AnchorSelection` — add lands
  on the created item by anchoring to `NewItemId`.)

## TUI wiring (`TaskDetailScreen`, build + `tui-validate` only)

- **Chords** `F7`/`F8`/`F9` → new `KeyAction.AddChecklistItem` / `RenameChecklistItem` /
  `DeleteChecklistItem`, in `Keybindings.cs` under `ScreenContext.Detail` and in **both** `HelpItemSets`
  Detail sets (`Detail` + `DetailWithTaskTree`), per #355. Dispatched in `OnKey` guarded to
  `ReferenceEquals(_tabs.Value, _checklistList)`, after the existing tree/Space guards. On a header /
  empty-state row, add targets the selected/owning checklist; rename & delete are inert-but-flashed
  ("select an item first"), matching the D header-row handling.
- **Add / rename input overlay** — a bottom-anchored single-line `FrameView` modelled on
  `ShowCommentComposer` (`_checklistItemBox` + `TextField` + Save/Cancel). Add → empty; rename →
  pre-filled with the item's current name and following the description editor's dirty/discard-confirm
  discipline (an accidental `Esc` on an edited name arms a `(Y / N)` confirm). Added to the top-of-`OnKey`
  overlay guard and given its own `HelpItemSets.DetailChecklistItemEditor` set wired through
  `DetailFooter` (a new `checklistItemEditorVisible` bool), mirroring `DetailCommentComposer`.
- **Delete confirm** — an inline armed `(Y / N)` prompt reusing the `_descriptionPendingDiscard` pattern
  (no modal; #404/#402 seam untouched). Cancelling leaves the item and selection untouched.
- **Optimistic + revert**, mirroring `ToggleSelectedChecklistItem` for all three:
  - *Add*: insert a provisional item (sentinel id) via `InsertProvisional`, re-project, select it; fire
    `CreateChecklistItemAsync`; on success replace the target checklist with the server-mapped one and
    select `NewItemId`; on failure remove the provisional, re-project, flash.
  - *Rename*: `SetName` optimistically (selection stays by id), fire `RenameChecklistItemAsync`, on
    success replace with the server checklist, on failure revert to the snapshot + flash.
  - *Delete*: compute `SelectAfterDelete`, `Remove` optimistically, fire `DeleteChecklistItemAsync`, on
    failure restore the snapshot + prior selection + flash.
  - Reuse the D in-flight flag / pending-overlay-in-`UpdateData` discipline so a 30 s auto-refresh or
    `F5` landing mid-write neither clobbers nor double-applies the edit.
- Present in both hosts (dashboard + single-task) — new ctor callbacks wired in `TodoApp` and
  `SingleTaskApp`, exactly like the D `setChecklistResolvedAsync` callback.

## Tests

- **Unit** `ChecklistItemEditsTests` (arranger-projection style like `ChecklistToggleTests`): rename a
  top-level item / a nested child / an item present flat + as a child; remove a leaf, remove a parent
  (children go too), remove updates aggregate counts; insert appends + re-projects; no-ops on
  missing/header; `NormalizeName` (trim, reject empty/whitespace); `NewItemId` diff.
- **Unit** `ChecklistTabModelTests` additions: `SelectAfterDelete` — next sibling, else previous, else
  header, else clamp; deleting the only item lands on the header.
- **Facade** `ClickUpClientChecklistWriteTests` additions (`CapturingHandler` style): create asserts
  `POST …/checklist_item`, `{name}` body, mapped result; rename asserts `PUT …/checklist_item/{id}`,
  `{name}` body; delete asserts `HttpMethod.Delete`, correct URL, no body.
- **Facade integration** (`SkippableFact`, `CLICKUP_TOKEN`-gated): create a task + checklist, add an
  item, rename it, delete it, asserting each server response; clean up. Skips without credentials.
- **`tui-validate`** (only after `dotnet test` is green): extend `checklist_check.py` with an E leg that
  drives `F7` add → type → Enter, `F8` rename → edit → Enter, `F9` delete → `Y`, asserting the pyte
  screen and the group/title counts move; confirm `detail_comment_check.py` / `description_edit_check.py`
  still pass (the chords E adds don't shadow theirs). `Program.cs` fake gains `POST`/`DELETE`
  `checklist_item` routes (create appends to `_checklistsDom` + echoes the parent; delete removes +
  returns empty); `RouteTableTests` stays green (non-ambiguous segment counts/methods).

## Phases

1. **Spec + regen + facade + service + pure transforms + unit/facade tests** — CI-green, no TUI surface.
   Push → draft PR.
2. **TUI wiring** — the three chords, the add/rename overlay, the delete confirm, optimistic + revert,
   keybindings + help, host wiring. Build; then `tui-validate`.

## Hard rules honored

- **No hand edits under `Generated/`** — spec edit + `dotnet kiota generate` only.
- Generated types never escape the facade; the domain carries `TaskChecklist`/`TaskChecklistItem`.
- Personal-token raw `Authorization` header untouched; the API-boundary test is a `SkippableFact`.
- Single sectioned `ListView` main-list model untouched; no second focusable pane (#3) — this rides the
  existing Checklists tab `ListView` from #456.
- One source of truth for shortcuts: the new chords land in `Keybindings.cs` **and** both `HelpItemSets`
  Detail sets, cross-checked by `KeybindingsTests`.
- Optimistic write + revert-on-failure + flash, matching the D toggle / composer / editor pattern.
