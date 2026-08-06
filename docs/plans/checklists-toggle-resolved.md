# Plan — Checklists (D, #457): `Space` toggles an item resolved, optimistic with revert-on-failure

Slice **D** of the Task Checklists epic (#453). Depends on **A** (#454, read model + `TaskDetail.Checklists`)
and **C** (#456, the Checklists tab) — both merged. This is the epic's highest-value write and completes
its minimum-useful slice ("see the checklist, tick things off"). Out of scope: item CRUD (**E**), group
CRUD (**F**), assignee/reorder (**G**).

## What exists to build on

- **Read model** (#454): `TaskDetail.Checklists : IReadOnlyList<TaskChecklist>`; `TaskChecklist`
  (id/name/orderindex/resolved/unresolved/items) and `TaskChecklistItem`
  (id/name/resolved/orderindex/parentId/assignee/children). Mapped in `ClickUpClient.MapChecklist` via the
  pure `ChecklistReader` (items ride on Kiota `AdditionalData`, read back with `System.Text.Json`).
- **Projection** (#455): `ChecklistArranger.Project(checklists)` → `ChecklistProjection` (flat display-ordered
  `ChecklistRow`s + aggregate resolved/total). Each `ChecklistRow` carries `ChecklistId` + `ItemId` so a
  write slice never re-walks the tree from a selected index. **Progress is computed from the projected
  items, not the API's `resolved`/`unresolved` counts** — so the header/tab title stay self-consistent.
- **Tab model** (#456): `ChecklistTabModel.TabTitle` / `RenderRow` / `Signature` / `AnchorSelection`; the
  Checklists tab is a focusable `ListView` in `TaskDetailScreen`, modelled on the Task Tree tab (#291),
  refresh-safe via a content signature + item-id anchoring.

## API + facade

ClickUp's `PUT /v2/checklist/{checklist_id}/checklist_item/{checklist_item_id}` with body `{ "resolved": <bool> }`
sets an item's resolved state and returns the **whole parent checklist** wrapped as `{ "checklist": { … } }`
(id/name/orderindex/resolved/unresolved/items). Per #454, the `/checklist…` write paths were deliberately
left out of the curated spec until the slice that uses them — this slice adds exactly the one it needs.

- **Spec** (`clickup-openapi.json`): add the path with a `UpdateChecklistItemRequest` request body
  (`resolved: boolean` only — E adds `name`/`assignee`/`parent`) and a `ChecklistItemResponse` response
  (`checklist: $ref Checklist`). Reuse the existing `Checklist` schema (items stay untyped on
  `AdditionalData`, read by `ChecklistReader` exactly as the GET path does). Regen with
  `dotnet kiota generate …` (the `scripts/regen-client.ps1` command; `pwsh` is not required). **Never
  hand-edit `Generated/`.**
- **Facade** (`IClickUpClient` + `ClickUpClient`): `SetChecklistItemResolvedAsync(checklistId, itemId,
  resolved, ct)` → the server-confirmed `TaskChecklist` (mapped via the existing `MapChecklist`), matching
  the "return the truth" contract of `SetTaskStatusAsync`/`SetTaskDescriptionAsync`. Default throwing
  interface implementation so read-only fakes needn't implement it. Generated types stay behind the facade.
- **Service** (`TaskService`): a thin passthrough `SetChecklistItemResolvedAsync`, mirroring
  `SetTaskDescriptionAsync`, so the screen depends only on `TaskService`.

## Pure toggle transform (CI-testable)

`ChecklistToggle.SetResolved(IReadOnlyList<TaskChecklist> checklists, string checklistId, string itemId,
bool resolved)` → a new immutable `IReadOnlyList<TaskChecklist>` with exactly the matching item's `Resolved`
flipped, in **both** the flat `Items` list and any nested `Children` (an item ClickUp expresses in both is
updated consistently). A non-matching checklist/item is an identity no-op. Pure, order-preserving, no
Terminal.Gui. This is the count-recomputation + revert foundation:

- Re-projecting the toggled checklists via `ChecklistArranger.Project` yields the new header/aggregate
  counts (the arranger already derives progress from the items) — so "counts update" is tested by
  toggle-then-project.
- Revert = `SetResolved(..., !resolved)` (or restoring the snapshot) restores the exact prior projection —
  tested as round-trip identity.

The `TaskChecklist.Resolved`/`Unresolved` API counts are left untouched by the transform (the arranger
ignores them); documented so a reviewer doesn't read it as a bug.

## TUI wiring (`TaskDetailScreen`, build + `tui-validate` only)

- **Chord `Space`** → a new `KeyAction.ToggleChecklistItem`, registered in `Keybindings.cs` under
  `ScreenContext.Detail` and in **both** `HelpLine` sets (`Detail` and `DetailWithTaskTree`), per #355; the
  cross-check tests stay green. `Space` is free on `ScreenContext.Detail` and costs nothing on the read-only
  text panes.
- **Guarded to the Checklists tab being front-most**, using the same `ReferenceEquals(_tabs.Value, …)` shape
  the tree tab's `Enter`/`F6` handlers use — so `Space` on the other tabs is unchanged.
- **On a header row (or the empty-state row), `Space` is inert** and flashes a short "nothing to toggle
  here" note rather than silently no-op'ing ambiguously.
- **Optimistic update**: flip the selected item in the screen's working `TaskDetail.Checklists` via
  `ChecklistToggle.SetResolved`, re-project, re-render the list and the tab title immediately (before the
  request), keeping the selection anchored to the same item id.
- **Off-thread write** via an injected `Func<…, Task<TaskChecklist>>` callback (the screen takes callbacks,
  it never reaches for the client), wired by the host to `TaskService.SetChecklistItemResolvedAsync`.
- **Revert-on-failure**: on exception, restore the pre-toggle checklists snapshot, re-project/re-render, and
  flash the error. The screen stays responsive throughout.
- **Refresh race**: reuse the existing `_savingDescription`-style in-flight flag so a 30 s auto-refresh or
  `F5`/`Ctrl+R` landing mid-toggle neither undoes nor double-applies it, nor resurrects the stale `resolved`
  value the write is in the middle of changing.
- Present in both hosts (dashboard + single-task launch), since the Checklists tab already is.

## Tests

- **Unit** (`ChecklistToggleTests`): flip a top-level item; flip a nested child (Children representation);
  flip an item expressed as both flat + child; no-op for unknown checklist/item; toggle-then-toggle-back is
  identity (revert); re-projected header/aggregate counts move by exactly one.
- **Facade integration** (`SkippableFact`, gated on `CLICKUP_TOKEN`): create a task, add a checklist + item,
  toggle it resolved, assert the returned `TaskChecklist` reflects it; clean up. Skips without credentials.
- **`tui-validate`** (after `dotnet test` is green): extend `checklist_check.py` to send `Space` on an item
  row and assert the glyph `[ ]`→`[x]` and the group `(r/t)` + tab-title aggregate change on the pyte screen;
  assert `Space` on a header row does not change a glyph. `E2E_CHECKLISTS` fake backend gains a
  `PUT /checklist/{id}/checklist_item/{id}` route returning the updated checklist JSON.

## Phases

1. **Spec + regen + facade + service + pure toggle + unit/facade tests** — CI-green, no TUI surface. Push →
   draft PR.
2. **TUI wiring** — `Space` toggle on the Checklists tab (optimistic + revert + refresh guard), keybinding +
   help. Build; then `tui-validate`.

## Hard rules honored

- **No hand edits under `Generated/`** — spec edit + regen only.
- Generated types never escape the facade; the domain carries `TaskChecklist`/`TaskChecklistItem`.
- Personal-token raw `Authorization` header untouched. The API-boundary test is a `SkippableFact`.
- Single sectioned `ListView` main-list model untouched; no second focusable pane (#3) — this is the
  existing Checklists tab, a focusable `ListView` already present since #456.
- One source of truth for shortcuts: the new chord lands in `Keybindings.cs` **and** both `HelpLine` sets.
- Optimistic write + revert-on-failure + flash, matching the composer/editor/Quick Updates pattern.
