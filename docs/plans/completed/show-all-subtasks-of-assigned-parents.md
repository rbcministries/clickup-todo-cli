# Plan — Show all subtasks of assigned parents regardless of assignee (#70)

## Goal

Add an opt-in view setting that, when on (and the F4 subtasks view is on), nests **all** of a parent's
subtasks under it whenever the parent is in my view — **regardless of who each subtask is assigned to**.
When off (today's behaviour), only subtasks that independently qualify for the snapshot (assigned to me /
my Assignee-IS set) are nested.

Dependency #68 (Assignee as a first-class filter, server-side) is **merged** (#72/#79). The assignee
constraint is enforced server-side in `GetAssignedTasksAsync`; `TaskView.Matches` explicitly no-ops
`TaskField.Assignee` — so pulled-in teammate-owned children are **not** re-filtered out client-side, while
Status/other filters still apply to them.

## Fetch strategy — no Kiota regen (the key decision)

The issue offers two fetch options. The per-parent `GET /task/{id}` child-include would require adding a
`subtasks` array + query param to the curated spec and **regenerating the Kiota client** (the `Task` schema
has no `subtasks` field; `GetTask` takes only `task_id`). Instead we use the issue's explicitly-sanctioned
**list-scoped fetch**: reuse the existing `GetListTasksAsync` (`GET /list/{id}/task` with `subtasks=true`,
no assignee filter), which already returns every task in a list — any assignee — with `parent` set. This
touches **neither `Generated/` nor the curated spec**.

Cost tradeoff (noted in PR): we fetch each distinct list that holds an in-view task. It's opt-in (off by
default) and only runs when both this flag and `ShowSubtasks` are on; best-effort per list.

## How nesting falls out for free

`SubtaskArranger.Arrange` already nests any child whose parent is present in the section. So the pulled-in
children just need to be **added to the non-pinned set fed to `TaskView.Apply`** before the per-group
arrange (as the issue requires). Their parent (assigned to me) is already in that set, so the arranger nests
them — grandchildren included, recursively — with **no arranger change**. In grouped views the documented
#57 rule applies: child nests when it shares the parent's group; otherwise it renders flat in its own group
(most visible under group-by-Assignee, since a teammate child sorts to a different assignee bucket).

## Phases

### Phase 1 — Setting + pure resolver (+ tests)
- `ViewSettings.ShowAllSubtasksOfAssignedParents` (bool, default false); fold into `IsDefault`
  (a set flag ⇒ not default). No config migration needed (new bool defaults false).
- `TaskService.ForeignDescendants(snapshot, listTasks)` — **pure**: given the snapshot and the tasks
  fetched from the snapshot's lists, return the tasks that (a) aren't already in the snapshot and
  (b) chain up through `parent` to a snapshot task — i.e. the teammate-owned descendants to pull in.
  Deterministic (fetched order), cycle-guarded, deduped by id.
- Tests: `ForeignDescendantsTests` mirroring `MissingParentIdsTests` (direct child pulled; grandchild
  pulled; already-present skipped; unrelated same-list task ignored; no-parent ignored; cycle-safe;
  empty). `ViewSettingsConfigTests` — flag round-trips and flips `IsDefault`.

### Phase 2 — Fetch wiring in the service (+ test)
- `TaskService.ResolveForeignSubtasksAsync(snapshot, ct)` — distinct non-blank `ListId`s → best-effort
  `GetListTasksAsync` per list → `ForeignDescendants`. A list that fails to fetch is skipped.
- Unit-test the id-selection via the pure helper (the fetch glue is thin/best-effort like
  `ResolveContextParentsAsync`, which has no direct unit test either).

### Phase 3 — TUI wiring (build + reasoning; Terminal.Gui not unit-testable)
- `TodoApp._foreignSubtasks` (volatile `IReadOnlyDictionary<string,TaskItem>`, keyed by id), mirroring
  `_contextParents`. Resolved in `FetchAsync` only when `ShowSubtasks && ShowAllSubtasksOfAssignedParents`.
- `Render`: when nesting, append `_foreignSubtasks.Values` to the non-pinned set before `TaskView.Apply`.
- `CurrentSignature`: fold in foreign child id:status so a change re-renders (like `_contextParents`).
- Not-mine marker: `TaskRowFormatter.Format` gains `isForeignSubtask` → appends
  `· (not assigned to you)`; threaded through `BuildRow`/`AddTask`; set for rows whose id is in
  `_foreignSubtasks`. Covered by `TaskRowFormatterTests`.
- Guards: `OpenStatusPicker` and `TogglePin` refuse foreign rows (they're not my work), mirroring the
  existing context-parent guard, each with a clear flash. (Enter/open-in-browser stays allowed.)
- F3 screen: a Button-toggle (mirroring the existing `dirButton` pattern — no new Terminal.Gui control)
  for the flag; `Save` composes it; label notes it needs F4 subtasks on. Help/footer text updated.

### Phase 4 — Layout test + finalize
- `SectionLayoutTests`/`SubtaskArrangerTests`: a foreign child (ordinary task w/ `ParentId`, parent
  present) nests under its parent in **grouped and ungrouped** views (the issue's "expand all children"
  coverage).
- Full gate: `dotnet build -c Release` (0/0), `dotnet test -c Release`, `dotnet format`.

## Non-goals / deferred (note in PR, file issues if none exist)
- Pinned parent + foreign children composition (#75): a foreign child whose parent is **pinned** (in the
  Focus section, not the non-pinned set) renders flat rather than nested under the pin. Minor; note it.
- Per-parent targeted fetch (avoids whole-list fetches) — needs the spec/regen route; revisit if the
  list-scoped cost bites.
