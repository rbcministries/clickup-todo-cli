# Plan — Issue #57: Nest subtasks within an active F3 group

Follow-up / deferred from #46 (PR #50). Today `nest = view.ShowSubtasks && !grouped`,
so when an F3 group field is active subtasks render **flat within each group**. #57 makes
nesting and field-grouping **compose**.

## Chosen semantics — option 1 (from the issue)

Within each F3 group, run the existing pure `SubtaskArranger` over just that group's rows:

- A subtask whose parent is **in the same group** → nested (indented) under it.
- A subtask whose parent is in a **different group** (assigned to me, so in the snapshot but a
  different group value) → renders **flat** at the top level of its own group. ("Leave orphaned
  subtasks flat.")
- A **context parent** (a parent not assigned to me, resolved via `ResolveContextParentsAsync`)
  → injected as a header wherever its children appear, per group. This resolves the issue's
  "a context parent has no natural group value" ambiguity: it isn't independently grouped, it
  rides along with its children.

The issue's own implementation note points squarely at option 1: *"Extend `TodoApp.Render`
(currently `nest = view.ShowSubtasks && !grouped`) and reuse the pure `SubtaskArranger` per
group."* The alternative (option 2 — group by the parent as an implicit dimension, parent
headers becoming the groups) is a much larger UX rethink and is **deferred** (see below).

## Why this is nearly free

`SubtaskArranger.Arrange(orderedTasks, contextParents)` is pure over *whatever* list it's
given. Called per-group it already yields option-1 semantics: a parent not in the passed-in
group list is "absent from section", so it falls to the context-header branch (if resolvable)
or the flat top-level branch (otherwise) — exactly what we want. No arranger change needed.

## Changes

### 1. `Services/SectionLayout.cs` (new, pure) — the testable seam
A pure helper that lays out the to-do section (headers + task rows) so the grouping×nesting
composition is unit-testable (the arranger itself already is; the *composition* in `Render`
isn't, because `Render` touches Terminal.Gui). Returns an ordered list of `LayoutRow`
(a header row, or a task row with depth + context-parent flag).

```csharp
public readonly record struct LayoutRow(string? HeaderText, TaskItem? Task, int Depth, bool IsContextParent)
{
    public bool IsHeader => Task is null;
}

public static IReadOnlyList<LayoutRow> BuildTodoSection(
    IReadOnlyList<TaskGroup> groups,
    IReadOnlyDictionary<string, TaskItem> contextParents,
    bool grouped, bool nest, bool hasPinnedSection, int todoCount, string tasksHeaderPrefix);
```
Mirrors the exact header logic currently in `Render` (a header per group when grouping;
otherwise the single `TASKS (n) ─` header only when a pinned section sits above it), and nests
per group when `nest` is true. Group header counts stay `group.Tasks.Count` (real members;
injected context parents aren't counted — consistent with the ungrouped path).

### 2. `Tui/TodoApp.Render`
- `var nest = view.ShowSubtasks;` (was `&& !grouped`). Update the stale "grouping wins" comment.
- Replace the inline group/header/nest loop with a `SectionLayout.BuildTodoSection(...)` call,
  materializing each `LayoutRow` via `AddHeader` / `AddTask`.

### 3. `Tui/TodoApp.FetchAsync`
- Resolve context parents whenever `ShowSubtasks` is on (drop the `&& GroupField is null` gate),
  since grouped mode now nests too. Update the comment.

## Out of scope / deferred
- **Option 2** (group-by-parent as an implicit dimension). Larger rethink; if wanted, file/keep
  a follow-up issue. Note in the PR.
- Nesting the **pinned** section (still flat — explicit pins; unchanged from #46).

## Phases
1. `SectionLayout` (pure) + unit tests (grouped headers, same-group nest, cross-group flat,
   context-parent injection per group, ungrouped-with-pins header parity). Wire `Render` +
   `FetchAsync`. → draft PR.
2. Build/test/format green (0 warn/0 err), review subagent, mark ready.

## Hard-rule checkpoints
- No `Generated/` edits, no spec change (no new API fields — pure presentation change).
- Raw `Authorization` auth untouched.
- Single sectioned `ListView` only — no second focusable pane (#3). Nesting stays
  indentation + header rows in the existing `_rows`/`_display`/`_badges`/`_depths` mechanism.
- Command keys unchanged (F4 already owns the toggle).
- Tests land with the code (pure `SectionLayout` unit-tested; TUI verified by build + notes).
