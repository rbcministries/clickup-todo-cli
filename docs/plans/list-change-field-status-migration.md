# Plan — #365: Field/status handling when changing a task's list membership (re-enable the Quick Updates List pane)

> **Goal (from the issue).** The Quick Updates **List pane** (#242/#339) and the New Task
> **multi-list create** (#241/#366) both shipped **disabled** because changing a task's list can
> strand custom fields / statuses that don't exist on the target list, and ClickUp's PWA offers a
> guided migration we didn't have. This plan designs that handling — **grounded in what ClickUp's API
> actually supports** — and re-enables the List pane behind it, with no silent field/status loss.
>
> **Delivery status.** Phase 1 (this design note) and Phase 2 (the id-precise, unit-tested
> `ListMembershipMigration` core + `CustomFieldItem.Id`) landed in an earlier PR (#400). **Phase 3 —
> the pane re-enable + confirmation UX + `tui-validate` — is now in progress** (see "Phase 3 decisions
> (this session)" below for the confirmation-UX choice and the concrete wiring).

## Phase 3 decisions (this session)

- **Confirmation UX: arm/confirm on the status line, not a modal.** Of the three candidate shapes below,
  the re-enable uses the **two-step "press remove again to confirm"** arm/confirm. The TUI is
  deliberately a single-screen, no-`MessageBox` model (notable outcomes surface through the
  non-interactive `Flash` line, tied to the #3 single-focusable-pane constraint); introducing the app's
  first modal in an unattended run is exactly what the original deferral cautioned against. Arm/confirm
  keeps that model **and** composes cleanly with the selector's reconcile-from-server contract: the
  arming turn returns the membership *unchanged*, so `SelectorView.Reconcile` re-shows the removed row
  and nothing is written until the confirming turn. A stranding remove of an additional list therefore
  flashes *"Removing '{list}' hides these Custom Field values: A, B. Press remove again to confirm."* and
  re-shows the row; a second remove of the same list within the pane writes it. (Maintainer: if you'd
  rather this be a real `MessageBox.Query`, say so on the PR — the decision is isolated in the pure
  `ListMembershipApplyPlanner` + a single armed-state field in `TodoApp`, so swapping is a small change.)
- **The decision is a pure, unit-tested planner.** `Tui/ListMembershipApplyPlanner.Plan(kind, list,
  homeListId, strandedFieldNames, armed)` → `WriteAdd` / `WriteRemove` / `BlockHomeRemove` /
  `ArmRemoveConfirmation`. The host computes `strandedFieldNames` via
  `ListMembershipMigration.StrandedFieldsOnRemove` (a preflight of the task's values + each list's field
  defs) and holds the `armed` (taskId, listId) state; the planner picks the action + flash text. Covered
  by `ListMembershipApplyPlannerTests` (add-always-safe, home-guard-wins, strand→arm→confirm, ordinal id
  match).

## The key distinction the issue conflates: **add-to-list** vs **move**

The issue body says "when a task **moves into (or is added to)** a list … status/fields that don't
exist on the target list." Those are two *different* ClickUp operations with *different* API surfaces
and *different* hazards. Grounding in the API (see Sources) is what makes this tractable:

| | **Add to List** (Tasks in Multiple Lists) | **Move to a new List** |
|---|---|---|
| Endpoint | `POST /list/{list_id}/task/{task_id}` (#237 facade) | `POST` *move-a-task-to-a-new-list* (separate endpoint) |
| What it changes | Adds an **additional** location; **home list unchanged** | **Changes the home list** |
| Status | Task **keeps its home List's status**; no remap | May need `status_mappings` (current status absent on dest) |
| Custom fields | Task **keeps all its values** and **gains access** to the target list's fields | `move_custom_fields` / `custom_fields_to_move` decide what carries over |
| Guided PWA flow | none needed | the status/field mapping modal |

**The Quick Updates List pane and the New Task multi-list create use only the add/remove
(additional-location) writes** — `AddTaskToListAsync` / `RemoveTaskFromListAsync` (#237). The
home-list **move** is explicitly **out of scope** (home is set only at create, #209/#237; the #242
plan's "Deferred" section already says so). That means the heavyweight PWA migration
(`status_mappings`, `move_custom_fields`) does **not** apply to what this pane does.

### Consequences (what the hazard actually is)

- **Status: no handling required.** A task in multiple lists *always* uses its **home** List's
  status (ClickUp Help: "Tasks in Multiple Lists will always use the statuses from their primary home
  List"). Adding or removing an *additional* location never remaps or drops the status. The
  "status doesn't exist on the target list" modal is a **move**-operation concern, out of scope here.
- **Add: no data loss.** Adding a task to an additional list keeps every existing field value and
  merely *exposes* that list's fields to the task (Help: "Tasks inherit the Custom Fields from their
  locations"). So **add proceeds without a confirmation.**
- **Remove: the one real hazard.** Removing a task from an additional list drops the task's access to
  any Custom Field that **only that list defined**. A *value* set on such a field is no longer
  surfaced — silent from a one-keystroke pane. So **remove needs a preflight + confirmation** when it
  would strand a set value, and must **proceed silently when it strands nothing**.
- **Removing the home list is not an additional-location remove.** It would require giving the task a
  new home (a *move*), which is out of scope. The pane must **block it (flash), never silently attempt
  it.** (The selector currently marks the home list removable "(home)"; the host guards the write.)

## Design

### Preflight (only on a remove that could strand)

To decide whether a remove strands anything, compare the task's **set** field values against the
Custom Field **definitions** of the task's lists:

- Task's set values: `TaskDetail.CustomFields` (`CustomFieldItem`). This record currently drops the
  field **id**; we add it (the generated `CustomField` already carries `id`) so matching is
  **id-precise**, not name-based.
- Per-list definitions: `GetListCustomFieldsAsync(listId)` (#249) — `GET /list/{id}/field` returns
  every field **accessible** on that list, *including inherited Space/Folder-level fields*. That's the
  property that makes detection robust: a Space-level field appears in **every** list's response, so
  it is **never** flagged as stranded — only truly list-local fields can be.

**Strand rule.** For a proposed remove of list `R` from a task that will still belong to lists
`Remaining`: a field value is *stranded* iff the field's id is present in `R`'s definitions and in
**none** of `Remaining`'s definitions, **and** the task has a non-empty value for that field. (A field
with no value set can't lose data.)

### Pure, unit-tested core — `Services/ListMembershipMigration.cs`

Terminal-free static logic, tested with xUnit (mirrors `NewTaskCreator`):

```csharp
public static class ListMembershipMigration
{
    // Field NAMES whose set values would be stranded by removing `listToRemove`.
    public static IReadOnlyList<string> StrandedFieldsOnRemove(
        IReadOnlyList<CustomFieldItem> taskFields,                 // task's values (need Id + HasValue)
        string listToRemove,
        IReadOnlyDictionary<string, IReadOnlyList<CustomFieldDefinition>> perListDefinitions,
        IReadOnlyCollection<string> remainingListIds);            // task's lists minus listToRemove

    // True when a value is actually set (non-null / non-empty JSON), so an unset field never strands.
    public static bool HasValue(CustomFieldItem field);
}
```

- Ids compared with `StringComparer.Ordinal`; blank ids ignored.
- A list absent from `perListDefinitions` (fetch failed) is treated **conservatively** — see below.
- Returns distinct field **names** (for the confirmation text), order preserved from `taskFields`.

Supporting model change: `CustomFieldItem` gains `string? Id` (trailing optional param — the 3
existing constructor call sites stay source-compatible); `ClickUpClient.MapCustomField` passes
`f.Id`. `HasValue` centralises "is this value actually set" using the existing `Value` `JsonElement?`.

### UX — as shipped (arm/confirm on the status line)

The List pane embeds a `ListSelectorView` in `ImmediateApply` mode; a toggle calls the host
`ApplyListAsync(taskId, kind, list, ct)`. As implemented (see "Phase 3 decisions (this session)"
above for why arm/confirm over a modal), that method:

- **kind == Added** → write immediately (add is always safe); read the confirmed membership back. No
  detail preflight is fetched on the add path.
- **kind == Removed & the home list** → do **not** write; `Flash`
  `ListMembershipApplyPlanner.HomeRemoveMessage`; return the unchanged membership so the selector's
  reconcile re-shows the `(home)` row.
- **kind == Removed & an additional list**:
  1. Fetch the `TaskDetail` once (home + additional locations + set field values). When not already
     armed, preflight `GetListCustomFieldsAsync` for the removed + remaining lists and compute
     `StrandedFieldsOnRemove`.
  2. **Nothing stranded** → write the remove; read back.
  3. **Something stranded, not yet armed** → don't write: `Flash` the affected field names + "Press
     remove again to confirm", **arm** `(taskId, listId)`, and return the membership *unchanged* so the
     row re-shows (the selector's reconcile-from-server rebuilds the optimistically-removed row).
  4. **Armed** (the confirming second press) → clear the arm, write the remove, read back.

The arm state is a single `TodoApp._armedListRemoval` `(taskId, listId)?` field, cleared on any write
and reset on each `ShowQuickUpdates` open (so a stale arm can't skip the warning). Flashes are
marshalled to the UI thread (`FlashOnUi` → `Application.Invoke`). A failed preflight fetch degrades to
the **safe** side inside `StrandedFieldsOnRemove` (a list whose defs are absent is treated
conservatively), so the confirmation still fires rather than a blind silent write. A disabled "Tasks in
Multiple Lists" ClickApp surfaces as a flashed `ClickUpApiException` via the selector's revert path
(non-fatal).

## Phases

1. **Design note** (this file). Commit → opens draft PR.
2. **Model + pure service + tests.** `CustomFieldItem.Id`; `ListMembershipMigration` +
   `ListMembershipMigrationTests` (strand computation: space-level never flagged; unset never
   flagged; list-local with value flagged; multi-list remaining coverage; blank-id ignored; missing
   definitions ⇒ conservative). Landed in PR #400.
3. **Re-enable the pane (this session).** Added the pure `ListMembershipApplyPlanner` + tests;
   re-enabled the `#242` blocks in `QuickUpdatesModel` (`Lists` + `PaneCount` 4), `QuickUpdatesScreen`
   (field, ctor params, construction, `listsFrame`, `_panes`/`Add`, `SeedListMemberships`, geometry),
   `HelpLine`, and `TodoApp.ShowQuickUpdates` (seed, ctor args, enrich); wired `ApplyListAsync` to the
   preflight + planner + arm/confirm + home-guard; and rewrote `qu_lists_check.py` from the
   disabled-state guard into the add/remove round-trip + confirm-on-strand + home-guard, teaching the
   fake backend per-list field definitions + task field values (`E2E_QU_LISTS`-gated).
   (`TaskService.GetListCustomFieldsAsync` already existed from the New Task custom-field work.)

New Task multi-list create (#241) re-enable rides the same handling but is a **separate** screen and
its own follow-up (tracked below) — this issue's AC is the **Quick Updates List pane**.

## Invariants preserved

- **No `Generated/` hand-edit, no curated-spec change.** Both writes (#237) and the field-definition
  read (#249) already exist. `CustomFieldItem.Id` is a facade-model change, not generated code.
- **No second focusable pane (#3/#38).** The List selector is a single focusable composite; the
  confirmation is a status-line `Flash` (arm/confirm), not a persistent pane or a modal.
- **Bare letters reserved for type-ahead (#12).** The selector owns its own search box.
- Integration tests stay `SkippableFact`; the migration logic is carried by unit tests.
- Personal-token raw `Authorization` header untouched.

## Deferred (tracked, linked from the PR)

- **New Task multi-list create re-enable (#241/#366)** — apply the same add-is-safe handling on the
  New Task screen (add-only there: create in home list + add to each extra list, all adds are safe, so
  no confirmation is even needed — only the `MultiListDisabledNote` gate is dropped). Separate screen,
  separate follow-up issue.
- **Precise strand detection when a preflight fetch fails** — the conservative fallback shows a
  generic confirmation; a retry/caching refinement is a follow-up.

## Sources

- ClickUp Help — *Tasks in Multiple Lists* / *Statuses FAQ*: a task in multiple lists uses its **home
  List's** status; tasks **inherit** Custom Fields from their locations.
- ClickUp Developer — *Move a task to a new List*: `status_mappings`, `move_custom_fields`,
  `custom_fields_to_move` — the **move** operation's parameters (out of scope here).
- Repo: `docs/plans/quick-updates-list-pane.md` (#242, disabled), `list-custom-field-definitions-fetch.md`
  (#249), `new-task-multi-list-create.md` (#241, disabled).
