# Plan — #365: Field/status handling when changing a task's list membership (re-enable the Quick Updates List pane)

> **Goal (from the issue).** The Quick Updates **List pane** (#242/#339) and the New Task
> **multi-list create** (#241/#366) both shipped **disabled** because changing a task's list can
> strand custom fields / statuses that don't exist on the target list, and ClickUp's PWA offers a
> guided migration we didn't have. This plan designs that handling — **grounded in what ClickUp's API
> actually supports** — and re-enables the List pane behind it, with no silent field/status loss.
>
> **Delivery status.** The first PR on this issue lands **Phase 1 (this design note)** and **Phase 2
> (the id-precise, unit-tested `ListMembershipMigration` core + `CustomFieldItem.Id`)**. **Phase 3
> (the actual pane re-enable + its confirmation UX + `tui-validate`) is deferred** and remains tracked
> by #365 — see "Why Phase 3 is deferred" below. The design here is the reference the re-enable builds
> on.

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

### UX (re-enabling the pane behind the handling)

The List pane embeds a `ListSelectorView` in `ImmediateApply` mode; a toggle calls the host
`ApplyListAsync(taskId, kind, list, ct)` which today just writes and reads back. New behaviour:

- **kind == Added** → write immediately (unchanged; add is safe). Read back confirmed membership.
- **kind == Removed & list is the home list** → do **not** write; `Flash` "Can't remove a task's home
  list here (that's a move — not yet supported)"; return the unchanged membership so the selector
  reconciles the row back.
- **kind == Removed & additional list**:
  1. Preflight: fetch `GetListCustomFieldsAsync` for the task's lists (home + additional), fetch
     `TaskDetail` for the set values. Compute `StrandedFieldsOnRemove`.
  2. If **empty** → write the remove silently (no prompt); read back.
  3. If **non-empty** → `MessageBox.Query` (a transient modal, **not** a second focusable list — the
     #3 single-pane rule is about the main list) listing the affected field names: *"Removing from
     '{list}' will hide these Custom Field values: A, B. Remove anyway?"* On **No**, return the
     unchanged membership (selector reverts the row); on **Yes**, write + read back.

A failed preflight fetch degrades to the **safe** side: treat the remove as *potentially* stranding
(show the generic confirmation) rather than writing blindly — never silently.

The confirmation is driven from `ApplyListAsync` by marshalling to the UI thread
(`Application.Invoke` + a `TaskCompletionSource`) so the async apply awaits the user's choice; the
selector's existing revert-on-unchanged / reconcile-from-truth machinery does the rest. A disabled
"Tasks in Multiple Lists" ClickApp still surfaces as a flashed `ClickUpApiException` (non-fatal).

## Phases

1. **Design note** (this file). Commit → opens draft PR.
2. **Model + pure service + tests.** `CustomFieldItem.Id`; `ListMembershipMigration` +
   `ListMembershipMigrationTests` (strand computation: space-level never flagged; unset never
   flagged; list-local with value flagged; multi-list remaining coverage; blank-id ignored; missing
   definitions ⇒ conservative). Build + test green, push.
3. **Re-enable the pane. — DEFERRED (see "Why Phase 3 is deferred").** Uncomment the #242 blocks
   (`QuickUpdatesScreen`, `QuickUpdatesModel`, `HelpLine`, `TodoApp`; the `ListSelectorModel` /
   `ListSelectorView` / `SelectorView` / `TaskService` / `ClickUpClient` pieces are already active);
   wire `ApplyListAsync` to the preflight + confirmation + home-guard; restore the `qu_lists_check.py`
   `tui-validate` scenario from PR #339's history and extend it for the confirm-on-strand and
   home-guard cases. Finalize, mark ready.

New Task multi-list create (#241) re-enable rides the same handling but is a **separate** screen and
its own follow-up (tracked below) — this issue's AC is the **Quick Updates List pane**.

### Why Phase 3 is deferred (and what it needs)

Phase 3 is intentionally **not** in the first PR:

- **It introduces the app's first confirmation UX.** The TUI today has **no** `MessageBox`/modal
  anywhere — destructive/notable outcomes surface through the non-interactive `Flash` status line by
  design (a deliberate single-screen, no-modal model, tied to the #3 single-focusable-pane constraint).
  A "confirm before removing" step is therefore a genuinely new interaction pattern for this codebase.
  Which shape it takes — a real modal (`MessageBox.Query`), a two-step "press remove again to confirm"
  arm/confirm on the `Flash` line, or a non-blocking informational `Flash` after a reversible
  hide — is a product decision worth landing in its own focused, reviewable PR rather than choosing
  unilaterally in an unattended run. (The three candidates are recorded here so the follow-up can pick
  one quickly.)
- **It needs new fake-backend + `tui-validate` surface.** Restoring `qu_lists_check.py` from PR #339's
  history and extending it for the confirm-on-strand and home-guard cases requires the fake ClickUp
  backend to model **per-list Custom Field definitions** and **task field values** (it currently models
  list `locations` only). That is a substantial, terminal-only chunk best validated on its own.
- **The foundation it depends on is now in place and tested.** `ListMembershipMigration` +
  `CustomFieldItem.Id` (Phase 2) give the re-enable an id-precise, unit-tested strand check to call, so
  Phase 3 becomes wiring + UX + `tui-validate` — no new detection logic to design under a terminal.

Re-enable checklist for the follow-up (all gated behind `ListMembershipMigration`):
1. Uncomment the #242 blocks in `QuickUpdatesModel` (`Lists` value + `PaneCount` → 4), `QuickUpdatesScreen`
   (field, ctor params, construction, `listsFrame`, `_panes`/`Add`, `SeedListMemberships`, frame geometry),
   `HelpLine` ("Lists" in the Tab item), and `TodoApp.ShowQuickUpdates` (seed, ctor args, enrich).
2. Add a `TaskService.GetListCustomFieldsAsync` passthrough (currently only on `ClickUpClient`).
3. Wire `ApplyListAsync`: **add** → write directly; **remove of the home list** → block + `Flash`;
   **remove of an additional list** → preflight (`GetTaskDetailAsync` for values + `GetListCustomFieldsAsync`
   per list), call `ListMembershipMigration.StrandedFieldsOnRemove`, and apply the chosen confirmation UX
   when it returns a non-empty set (proceed silently when empty).
4. Restore + extend `qu_lists_check.py`; teach the fake backend per-list field defs + task field values.

## Invariants preserved

- **No `Generated/` hand-edit, no curated-spec change.** Both writes (#237) and the field-definition
  read (#249) already exist. `CustomFieldItem.Id` is a facade-model change, not generated code.
- **No second focusable pane (#3/#38).** The List selector is a single focusable composite; the
  confirmation is a transient `MessageBox`, not a persistent pane.
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
