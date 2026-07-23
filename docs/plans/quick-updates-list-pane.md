# Plan — #242: Quick Updates List pane (reuse `ListSelectorView`, immediate add/remove of list membership)

> **Status: implemented but temporarily DISABLED.** The pane and all its wiring are in the tree but
> commented out of the Quick Updates composition (`QuickUpdatesScreen`, `QuickUpdatesModel`,
> `HelpItemSets.QuickUpdates`, `TodoApp.ShowQuickUpdates`). Changing a task's list can strand custom
> fields / statuses that don't exist on the target list; ClickUp's PWA offers a **guided migration** for
> those cases and we don't yet, so enabling live list changes from Quick Updates would risk silent data
> loss. The reusable, side-effect-free pieces stay active: `ListSelectorModel.Membership`,
> `TaskService.Add/RemoveTaskFromListAsync`, `SelectorView.AddExistingSelections` /
> `ListSelectorView.SeedExistingMemberships`, and the fake-backend membership modeling. **Re-enable** by
> uncommenting the marked blocks in those four files (and restoring the add/remove `tui-validate`
> scenario from git history) once the field/status migration is designed — tracked in **#365**.

## Goal (from the issue)

Add a fourth Quick Updates pane that changes which list(s) a task belongs to, live, alongside
Status / Priority / Assignees. Reuses `ListSelectorView` (#239, merged) in `ImmediateApply` mode over
the list-frequency candidate pool (#238, merged), wired to the task↔list membership writes (#237,
merged) — mirroring how the Assignees pane (#158) embeds `AssigneeSelectorView`.

### Acceptance criteria

- Quick Updates shows a **List** pane seeded with the task's current lists; adding/removing a list
  writes immediately (optimistic + revert-on-failure) and the change reflects without a full reload.
- `Tab` reaches the pane; help text lists it; `dotnet test` green, then `tui-validate` confirms
  add/remove round-trips.
- The "Tasks in Multiple Lists" (#237) prerequisite is respected: a disabled-feature error is
  **flashed, not fatal**.

## Verified current state

- `QuickUpdatesScreen` stacks focusable panes cycled by `Tab`; it already embeds `AssigneeSelectorView`
  in `ImmediateApply` mode with `applyAsync` wired by the host. Panes: `[_statusList, _priorityList,
  _assignees]`, indexed by `QuickUpdatesPane { Status, Priority, Assignees }`.
- `QuickUpdatesModel` owns the pure pane-cycle logic (`Cycle`, `PaneCount`) and the Status/Priority
  rows; the Assignees pane's row logic lives in `SelectorModel` / `AssigneeSelectorModel`.
- `ListSelectorView` (#239) is ready in `ImmediateApply` mode: it takes `match` / `topFrequent`
  (`ListFrequencyCache.Match` / `TopMostFrequent`), `initialSelected`, a `primary` (the home/distinguished
  list, marked `" (home)"`, removable — no lock), and an `applyAsync(kind, list, ct)` returning the
  server-confirmed membership set. It's a single focusable composite (no second focusable pane — #3/#38).
- Membership writes: `IClickUpClient.AddTaskToListAsync` / `RemoveTaskFromListAsync` (#237). They
  **return no body**; the confirmed membership is read back via `GetTaskDetailAsync(...)` — `.ListId`/
  `.ListName` is the home list, `.Lists` the *additional* locations (empty in the common single-list case).
- Host: `ShowQuickUpdates` constructs the screen and wires the Assignees apply to `ApplyAssigneeAsync`.
  `TaskItem` (what Quick Updates holds) has only the single **home** list (`ListId`/`ListName`); the
  full membership (home + additional locations) is only in `TaskDetail`. Detail-origin launches
  (`OpenQuickUpdatesForDetail`) already hold a `TaskDetail` with `.Lists`; list-origin launches do not.

## Design

### Seeding the pane with current memberships

Full membership = **home list** (always known from `TaskItem`) + **additional locations**
(`TaskDetail.Lists`, only present on a `TaskDetail`).

- **Home** seeds as the pane's `primary` (the `" (home)"` marker) — always available, so the pane opens
  instantly and correctly for the **common single-list case** with no extra round-trip.
- **Additional locations** (the rare "Tasks in Multiple Lists" case):
  - **Detail origin** — seed them at construction from `detailScreen.Task.Lists` (free, no fetch).
  - **List origin** — the snapshot `TaskItem` has none, so enrich them via a background
    `GetTaskDetailAsync` fired right after open, merged into the pane when it returns.

Enriching an already-constructed `SelectorView` needs a minimal, additive API:

- `SelectorView.AddExistingSelections(items)` (protected): adds each not-already-selected item to the
  selection and re-renders, **without** firing a server write (these are pre-existing memberships, not
  user adds). Guarded to no-op once the user has interacted (`_applyGeneration > 0`) so a slow enrich
  can't resurrect a list the user just removed.
- `ListSelectorView.SeedExistingMemberships(lists)` (public): the list-typed wrapper the host calls.

### Immediate apply

Host `ApplyListAsync(taskId, kind, list, ct)`, mirroring `ApplyAssigneeAsync`:

1. `AddTaskToListAsync` / `RemoveTaskFromListAsync` off the UI thread (ct intentionally **not** threaded
   into the write — same reasoning as assignees: an Esc-cancelled token would drop an applied change).
2. The membership endpoints echo nothing, so read back the confirmed set with `GetTaskDetailAsync` and
   return `ListSelectorModel.Membership(home-from-detail, detail.Lists)` — the selector's `Reconcile`
   replaces its selection with this truth (self-healing; keeps the home marker if it's still present).

A disabled "Tasks in Multiple Lists" ClickApp surfaces as a `ClickUpApiException` from the write; the
`SelectorView` immediate-apply path catches it → reverts the optimistic change → `Flash` "Couldn't
update lists: …". Non-fatal, as required.

The main-list row shows only the **home** list (unchanged by additional-location edits), so — unlike the
Assignees pane — no row reconcile is needed.

### Pure, unit-tested logic

- `ListSelectorModel.Membership(NamedEntity? home, IReadOnlyList<NamedEntity> additional)` → the full
  membership set: home first (when its id is non-blank), then additional (non-blank ids, deduped by
  ordinal id, order preserved). Used both to compute the confirmed set and (conceptually) the seed.
- `QuickUpdatesModel`: add `QuickUpdatesPane.Lists = 3`; `PaneCount` 3 → 4; `Cycle` now covers four
  panes (Status → Priority → Assignees → Lists → Status).

## Phases

1. **Facade + pure logic** — `TaskService.AddTaskToListAsync`/`RemoveTaskFromListAsync` passthroughs;
   `ListSelectorModel.Membership`; `QuickUpdatesModel` 4-pane cycle. Unit tests for `Membership` and the
   new cycle order. Build + test green, push (opens draft PR).
2. **View + screen + host** — `SelectorView.AddExistingSelections` /
   `ListSelectorView.SeedExistingMemberships`; the List pane in `QuickUpdatesScreen` (frame, `_panes`,
   `Flash`, `SeedListMemberships`); `ShowQuickUpdates` wiring + `ApplyListAsync` + list-origin enrich;
   `HelpItemSets.QuickUpdates` gains a List entry. Build + test green, push.
3. **tui-validate** — extend the fake backend to mutate list `locations` on the membership POST/DELETE
   and reflect them in the detail `locations`; new `qu_lists_check.py` asserting the pane renders the
   home list marked `(home)`, `Tab` reaches it, and an add round-trips. Finalize, mark ready.

## Invariants preserved

- **Generated client / curated spec untouched** — no new ClickUp endpoint (the #237 facade already
  exists); no `Generated/` hand-edits.
- **No second focusable pane (#3/#38)** — the List selector is a single focusable composite embedded in
  the modal, added to the existing `_panes` Tab cycle.
- **Bare letters reserved for type-ahead (#12)** — the selector owns its own search box; no new
  bare-letter shortcut.
- Integration tests stay `SkippableFact`; unit tests carry the logic.

## Deferred

- Home-list **move** (changing the home list) stays out of scope, per #237 (home is set only at create,
  #209). The pane manages additional locations; a home-list remove rides the same write and, if ClickUp
  rejects it, is flashed non-fatally.
