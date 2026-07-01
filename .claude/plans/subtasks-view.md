# Plan — Issue #46: Subtasks view (F4 toggle, indented under parent, incl. non-assigned parents)

## Goal / acceptance (from the issue)

1. **Toggle with F4** (mirrors F1 help / F2 settings / F3 filter-sort-group). Default behaves as today.
2. **Render subtasks indented, directly beneath their parent** when the view is on.
3. A subtask whose **parent is not assigned to me** still appears under its parent, with the parent
   shown as a **visually-distinct header row** (pulled in purely as context).
4. Status-badge coloring stays correctly aligned on indented rows.
5. Footer/help text reflects the new F4 shortcut.
6. Ordering/grouping logic has unit tests.

### Open questions — decisions made here
- **Default state:** OFF = *behaves as today* (subtasks stay in the flat, due-sorted list as ordinary
  rows). ON (F4) = nested/indented under parent. The AC explicitly permits "hides them (or behaves as
  today)"; "behave as today" is non-destructive, so we keep the current rows visible by default and the
  toggle only changes the *arrangement*. Flag named **`NestSubtasks`** to name the behaviour accurately.
- **Persist the toggle?** Yes — in `ViewSettings.NestSubtasks` (persisted in `config.json`, like the
  other view settings).
- **Non-assigned parent header actions:** Enter (detail) and Ctrl+B/Ctrl+P work normally (it's a real
  task). **Space (status change) is a no-op** on a context parent (flash a note) — it's context, not my
  work.

## Hard-rule checkpoints
- `parent` is a new field → edit the **curated spec** `clickup-openapi.json`, then regen Kiota
  (`dotnet tool restore` + `dotnet kiota generate`, the `regen-client.ps1` body). Never hand-edit
  `Generated/`. Map `TaskObject.Parent` → `TaskItem.ParentId` in the `ClickUpClient` facade.
- Raw `Authorization` header auth untouched.
- **Single sectioned `ListView` only** — no second focusable pane (#3). Nesting is expressed as
  indentation + header rows in the existing `_rows`/`_display`/`_badges` mechanism.
- Command keys stay function keys/chords — F4 (not a bare letter, which is reserved for type-ahead #12).
- Tests land with the code: pure arranger + formatter are unit-tested; the parent-fetch boundary is a
  thin service method (its pure "which ids are missing" helper is unit-tested).

## Design

### 1. Data (spec + model)
- Curated spec: add `"parent": { "type": "string", "nullable": true }` to the shared `Task` schema
  (optional, so list endpoints are unaffected — same pattern as `date_created`/`priority` from #33).
- Regen the client.
- `TaskItem`: add `string? ParentId`. Map `t.Parent` in `ClickUpClient.Map`.

### 2. `ViewSettings`
- Add `public bool NestSubtasks { get; set; }` (default false). Fold into `IsDefault`.

### 3. Pure arranger — `Services/SubtaskArranger.cs`
```csharp
public readonly record struct ArrangedRow(TaskItem Task, int Depth, bool IsContextParent);
public static IReadOnlyList<ArrangedRow> Arrange(
    IReadOnlyList<TaskItem> orderedTasks,
    IReadOnlyDictionary<string, TaskItem> contextParents);
```
- Preserves the incoming (already filtered+sorted) order for top-level anchors; each task is followed
  by its descendants (recursive, so nested subtasks indent by depth).
- A subtask whose parent is **in** the section is emitted under that parent (parent-first regardless of
  the parent's position in the input).
- A subtask whose parent is **absent but resolvable** (in `contextParents`) → the parent is injected
  once as a `IsContextParent` row at the position of its first child, children indented beneath.
- A subtask whose parent is **entirely unknown** → shown flat as a top-level anchor.
- `emitted` guard makes it cycle-safe.

### 4. `TaskRowFormatter`
- `Format(TaskItem task, int depth = 0, bool isContextParent = false)`.
- Prefix `depth` levels of indent (2 spaces each). Shift `BadgeStart` by the indent width so the badge
  span stays exact.
- Context-parent rows get a trailing ` · (parent — not assigned to you)` marker for distinctness.

### 5. `TaskService`
- `internal static IReadOnlyList<string> MissingParentIds(IReadOnlyList<TaskItem> snapshot)` — distinct
  non-null `ParentId`s not present as an `Id` in the snapshot. **Pure, unit-tested.**
- `ResolveContextParentsAsync(snapshot, ct)` — fetch each missing parent via `GetTaskDetailAsync`,
  map `TaskDetail`→`TaskItem`, best-effort (skip per-id failures), return `id → TaskItem`.

### 6. `TodoApp` wiring
- `_contextParents` field. The `RefreshService` fetch func resolves context parents after each load
  **only when `NestSubtasks` is on** (off the UI thread; set before `onUpdate` marshals to UI).
- `OnListKey`: `case KeyCode.F4` → toggle `_config.View.NestSubtasks`, persist, `Render()` immediately
  (instant nest of in-snapshot parents), then `RequestRefresh()` (pulls context parents).
- `Render`: when `NestSubtasks && GroupField is null`, arrange the non-pinned section via
  `SubtaskArranger` and add rows with depth/context; otherwise existing behaviour (subtasks flat).
  **When an F3 group field is active, grouping wins and nesting yields** (can't group two ways at once)
  — documented limitation.
- Pinned section stays flat (explicit pins).
- `Space`: no-op + flash when the current row is a context parent (id in `_contextParents`).
- `BuildSignature`: include `ParentId` + nest state + context-parent ids so refreshes reconcile.
- Footer help + `HelpScreen` updated with F4.

## Phases
1. Spec + regen + `TaskItem.ParentId` + `ViewSettings.NestSubtasks` + config test. → draft PR.
2. `SubtaskArranger` + `TaskRowFormatter` depth/context + unit tests.
3. `TaskService` helpers + `TodoApp` F4/render/help wiring + tests.
4. Build/test/format green, review subagent, mark ready.

## Deferred (note in PR, file issues if needed)
- Nesting *within* an active F3 group (grouping currently wins).
- Nesting the pinned section.
</content>
