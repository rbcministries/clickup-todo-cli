# Plan — #85: nest pulled-in teammate children under a *pinned* parent too

Follow-up to #70 (PR #84). Makes `FocusSectionLayout` the single authority for what
gets pulled into the Current Focus section, so a **teammate-owned (foreign) subtask
whose in-snapshot ancestor is pinned** nests under that pinned parent instead of
disappearing.

## Problem (current behaviour)

`_foreignSubtasks` (#70) holds teammate-owned subtasks of in-view parents; they are
**not** part of the `_all` snapshot. `TodoApp.Render`:

- Builds Focus via `FocusSectionLayout.Build(_all, pinnedIds, …)` — walks `_all`
  only, so foreign subtasks never nest under a pinned parent in Focus.
- Builds the to-do foreign set via `TaskService.ForeignSubtasksNotUnderPinned(...)`,
  which **drops** any foreign subtask whose in-snapshot ancestor is pinned (so it
  doesn't render detached at the top of the to-do list).

Net effect: a foreign child of a **pinned** parent renders **nowhere** — dropped
from to-do, never added to Focus. `ForeignSubtasksNotUnderPinned` is explicitly
labelled *"interim for #85"* in `TaskService`/`ForeignDescendantsTests`.

### Why the interim helper isn't enough (the complementarity bug)

`ForeignSubtasksNotUnderPinned` decides "under a pin?" by walking to the **first
in-snapshot ancestor** and testing whether *that* is pinned. But an in-snapshot,
**non-pinned** task `M` can itself be nested into Focus because it descends from a
pinned `K` (the #75 in-snapshot pull). A foreign child `F` of `M` then has first
in-snapshot ancestor `M` (not pinned) → the helper **keeps** `F` in to-do, yet its
parent `M` now lives in Focus → `F` renders **detached** (or, if we naively also
pulled it into Focus, **twice**). The fix must make "pulled into Focus" and
"excluded from to-do" the *same* computation.

## Goal / acceptance criteria (from the issue)

- With F4 (`ShowSubtasks`) + `ShowAllSubtasksOfAssignedParents` on, a foreign subtask
  of a **pinned** parent nests **beneath that pin** in Current Focus, marked not-mine
  (`(not assigned to you)`), consistent with #75/#70.
- Such rows are **kept out of the to-do section** (no double render) — mirror the
  existing `_focusNestedIds` de-dup.
- "Expand all" must walk the snapshot, not the rendered rows — N/A here, but the
  pull must include foreign descendants regardless of fold state (collapsed ⇒ hidden,
  not relocated), matching the existing #76 behaviour for in-snapshot subtasks.
- Zero-config / F4-off behaviour unchanged.

## Design — make `FocusSectionLayout` authoritative

### `Services/FocusSectionLayout.Build` (new optional `foreignSubtasks` param)

```
FocusSection Build(
    IReadOnlyList<TaskItem> allTasks,
    IReadOnlySet<string> pinnedIds,
    bool nest,
    TaskField? sortField,
    SortDirection sortDirection,
    IReadOnlySet<string>? expanded = null,
    IReadOnlyList<TaskItem>? foreignSubtasks = null)   // NEW — #85
```

- When `nest` is true and `foreignSubtasks` is non-empty, build the parent→children
  map and the descend-from-pins DFS over the **union** of `allTasks` and
  `foreignSubtasks` (snapshot wins on id collision; foreign ids never collide with
  the snapshot by construction — `ForeignDescendants` excludes present ids — but
  de-dup defensively).
- The existing DFS then naturally pulls **every** descendant of a pin — in-snapshot
  *or* foreign, direct *or* transitive (incl. the `M` case above) — into `pulledTasks`
  / `NestedSubtaskIds`, and `SubtaskArranger` nests each under its real parent.
- `nest` off, or no foreign supplied ⇒ byte-for-byte identical to today (optional
  param defaulted; existing call sites/tests unchanged).

No change to `ArrangedRow`/`FocusSection` shape: the caller distinguishes foreign
rows for the marker via `_foreignSubtasks.ContainsKey(id)` (it already does this for
the to-do section).

### `Tui/TodoApp.Render`

- Pass `_foreignSubtasks.Values` into `FocusSectionLayout.Build` (only meaningful when
  `nest`; Build ignores it when `!nest`).
- Replace the `ForeignSubtasksNotUnderPinned(...)` call with the exact complement of
  what Focus pulled:
  `nonPinned = nonPinned.Concat(_foreignSubtasks.Values.Where(t => !focus.NestedSubtaskIds.Contains(t.Id)))`.
  This is complementary **by construction**, fixing the `M`-case detach/dup bug.
- In the Focus `AddTask` loop, pass
  `isForeignSubtask: _foreignSubtasks.ContainsKey(row.Task.Id)` so nested foreign rows
  get the not-mine marker (parity with the to-do loop).

### `Services/TaskService`

- Remove `ForeignSubtasksNotUnderPinned` — its responsibility now lives in
  `FocusSectionLayout` (it was labelled *interim for #85* and has no other callers).
  `ForeignDescendants` / `ResolveForeignSubtasksAsync` are untouched.

## Tests

- **Remove** the `ForeignSubtasksNotUnderPinned` section from
  `ForeignDescendantsTests.cs` (method deleted). `ForeignDescendants` tests stay.
- **Add** to `FocusSectionLayoutTests.cs` (pure, no UI), via a `foreignSubtasks`-aware
  `Build` helper:
  - Foreign child of a **pinned** parent nests at depth 1 and is in `NestedSubtaskIds`.
  - Foreign child of a **non-pinned** parent is **not** pulled (stays for to-do).
  - Transitive `M`-case: foreign `F` under non-pinned in-snapshot `M` under pinned `K`
    → `F` nested in Focus and in `NestedSubtaskIds` (the bug the interim helper had).
  - Foreign grandchild through a foreign intermediate under a pin → both nested.
  - Collapsed pinned parent hides its foreign child but still pulls it (de-dup holds).
  - No foreign / `nest` off ⇒ unchanged.
- All existing `FocusSectionLayoutTests` continue to pass unchanged.

TUI glue (`TodoApp.Render`, the marker) isn't unit-testable in CI — verified by build +
reasoning per the repo's TUI rule; no new focusable pane, no keybinding change. The
arrangement logic is fully covered by the pure `FocusSectionLayoutTests`.

## Out of scope

- Same-list fetch caveat for foreign subtasks (#86 / #87).
- Any change to how foreign subtasks are *fetched* (`ResolveForeignSubtasksAsync`).
</content>
</invoke>
