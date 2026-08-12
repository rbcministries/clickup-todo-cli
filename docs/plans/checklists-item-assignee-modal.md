# Plan — Checklists: per-item assignee inside the rename modal (#572)

Follow-up to **G (#460 / #568)** under the Task Checklists epic **#453**. #568 landed the assignee
**write core** — facade `ClickUpClient.SetChecklistItemAssigneeAsync` (set = user id, clear = explicit
`"assignee": null`), `TaskService.SetChecklistItemAssigneeAsync`, and the pure optimistic
`ChecklistItemEdits.SetAssignee` transform, all unit-tested — and **stubbed the UI hook** at
`TaskDetailScreen.RenameSelectedChecklistItem`. This plan builds the **UI half** #572 tracks: an assignee
control inside the checklist-item rename overlay, plus the host threading to feed it.

## What already exists on `main` (verified)

- **Read**: `TaskChecklistItem.Assignee` (`TaskAssignee?`), parsed by `ChecklistReader.ReadAssignee`; the
  row already renders the assignee suffix (the populated `checklist_check.py` leg asserts `Ada Lovelace`).
- **Write**: `TaskService.SetChecklistItemAssigneeAsync(taskId, checklistId, itemId, long? assigneeId, ct)`
  → server-confirmed `TaskChecklist`; `ChecklistItemEdits.SetAssignee(checklists, checklistId, itemId,
  TaskAssignee?)` (recurses into `Children`, value-identical no-op when the target is missing).
- **Shared selector**: `AssigneeSelectorView` / `SelectorView` (#243, "no fork"), already driven in
  `SelectorMode.ImmediateApply` in `QuickUpdatesScreen` over `AssigneeFrequencyCache.Match` /
  `TopMostFrequent` (returning `TaskAssignee`). `SelectorView.Reconcile` **replaces** the selection with
  the server-confirmed set after each immediate-apply write.
- **Host cache**: `TodoApp._assignees` (`AssigneeFrequencyCache`) exposes `Match`/`TopMostFrequent`;
  `SingleTaskApp._assignees` is the same cache, **nullable** (null when no host projected one — the
  single-task launch's F-inert path).
- **The rename overlay**: `ShowChecklistItemEditor` — a bottom-anchored `FrameView` (`_checklistItemBox`)
  with a single-line name `TextField` + Save/Cancel + a hidden discard-confirm row, Tab-cycled via
  `_checklistItemControls`. Reached today via the `F8` stopgap (`RenameSelectedChecklistItem`), migrating
  to `F2`/`Ctrl+E` under #541. The #538 contextual-chord decision (recorded) puts item editing — incl.
  the assignee — in this rename modal, and accepts native modals.

## Design decisions

### D1 — reuse `AssigneeSelectorView` in the rename overlay, Rename mode only

A fresh `AssigneeSelectorView` is created **each time** the overlay opens in `Rename` kind (so it is
re-seeded with *that* item's current assignee — the shared selector has no public "reset selection"), added
to `_checklistItemBox`, and disposed on hide. In the other overlay kinds (`Add` / `NewGroup` /
`RenameGroup`) no selector is shown and the overlay is byte-identical to today. This keeps the single
sectioned `ListView` model intact: the selector lives only inside a **transient modal** (exactly like Quick
Updates), so there is **no second persistent focusable pane** — the #3 input-latency regression does not
apply.

### D2 — `ImmediateApply`, screen-owned write; single-select via server reconcile

A checklist item has **one** assignee, but `SelectorView` is inherently multi-select with no single-select
mode. Rather than fork the selector, the assignee applies **immediately on pick** (`SelectorMode.
ImmediateApply`) through a screen-owned `applyAsync` that runs the same optimistic discipline as
`RenameChecklistItem`:

1. optimistically apply `ChecklistItemEdits.SetAssignee` to `_task.Checklists` and re-render the row;
2. `await` the host write delegate `SetChecklistItemAssigneeAsync(checklistId, itemId, assigneeId|null)`;
3. on success reconcile `_task.Checklists` from the server checklist (`ReplaceChecklist`) and **return the
   single confirmed assignee** (`[P]` for a set, `[]` for a clear) to the selector — `SelectorView.
   Reconcile` then collapses its selection to that one person, so picking a second person **replaces** the
   first (single-select falls out of server truth, no fork);
4. on failure revert `_task.Checklists` to the pre-write snapshot, then rethrow so the selector runs its own
   revert + flashes.

A `Remove` (deselecting the current assignee) writes `null` (clear). Set = the picked id.

**Timing note (flagged for the maintainer in the PR):** the issue text says "on save". Applying the
assignee **on pick** (not on the overlay's Save) is the choice that makes single-select correct without a
fork — the selector always shows exactly the server truth (one ✓), whereas a collect-on-Save selector would
show two ✓ for a single-assignee field and force a "last-wins" reduction. The name still commits on Save;
only the assignee is live (as it is everywhere else the selector is used). Flipping to collect-on-Save is a
contained change if the maintainer prefers the literal wording — called out for review.

### D3 — host threading; F-inert without a cache

`TaskDetailScreen` gains three optional constructor params:
`assigneeMatch: Func<string, ISet<long>, IReadOnlyList<TaskAssignee>>?`,
`assigneeTopFrequent: Func<int, ISet<long>, IReadOnlyList<TaskAssignee>>?`,
`setChecklistItemAssigneeAsync: Func<string, string, long?, CancellationToken, Task<TaskChecklist>>?`.
The assignee control appears only when **all three** are supplied. `TodoApp` wires them from `_assignees`
+ `_tasks` (passing the resolved task id, like `setChecklistResolvedAsync`); `SingleTaskApp` wires them
only when its nullable `_assignees` is present, else leaves them null so the rename modal is name-only
(F-inert assignee) — matching the issue's "single-task mode gates the pool on a supplied
`AssigneeFrequencyCache`".

## Phases

### Phase 1 — host threading + a pure save/seed helper + unit tests

- Add the three constructor params + backing fields to `TaskDetailScreen`; thread from both hosts.
- A tiny pure helper for the seed/reconcile edge the screen needs (e.g. reducing a server checklist's item
  to its single `TaskAssignee?`, and the current-assignee seed for a row) — unit-tested. (Most of the pure
  work — `SetAssignee` transform, facade set/clear — is already tested from #568; add only what's new.)

### Phase 2 — the rename-overlay assignee control + write orchestration

- Build the fresh selector on `Rename` open (seeded from the row's `TaskAssignee?`), grow/anchor the
  overlay, include the selector in the Tab focus ring + key routing (Tab/Esc/F1/Ctrl+Enter still handled by
  `OnChecklistItemKey`; typing/arrows/Enter-pick fall through to the selector), surface its `Flash`.
- The screen-owned `applyAsync` (D2); dispose the selector on hide.
- Update the Detail footer/help set only if a new key is introduced — none is (the control rides the
  existing rename surface), so `HelpItemSets` is untouched; the #355 cross-check stays green.

### Phase 3 — E2E + finalize

- Teach `ChecklistsScenario.ChecklistItemPut` to parse `{"assignee": id|null}`: set → replace the item's
  `assignee` with `{ id, username }` resolved from `FakeClickUp.Members`; null → clear to `null`.
- Extend `checklist_check.py`: open the rename overlay on an item, set an assignee (assert the row's suffix
  appears), clear it (assert it's gone), and confirm both survive `Ctrl+R`. Keep the existing legs green.
- `dotnet build -c Release` (0/0) → `dotnet test` → `dotnet format` → `tui-validate`.

## Acceptance criteria (from #572)

- [ ] The rename modal sets **and clears** the item's assignee; it persists in ClickUp and renders on the
      row (assignee suffix). — Phases 2/3
- [ ] The control is a specialisation of the shared `SelectorView` (#243) — no duplicated selector. — D1/D2
- [ ] A failed assignee write reverts to the exact prior state and flashes. — D2 (snapshot revert +
      selector revert)
- [ ] No new bare-letter chord; reached via the rename surface; #355 footer/keybinding cross-checks stay
      green. — D1 (no new key)
- [ ] `dotnet test` green first, then `tui-validate` (extended `checklist_check.py` + fake `{assignee}`
      handler). — Phase 3

## Out of scope / deferred

- The `F8` → `F2`/`Ctrl+E` rename-surface migration is **#541** (part of #537); this control rides it
  automatically since it lives in the shared `ShowChecklistItemEditor` overlay. No keybinding change here.
- Reorder / reparent (the other half of #460) is **#569** (PR #576).
