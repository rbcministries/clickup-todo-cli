# Omit the active F3 group field from the row layout when grouped (#67)

## Problem

Since #65, each to-do row renders as:

```
{name}  [status]  [priority]  · {list}  · due {date}
```

When an F3 group field is active (`ViewSettings.GroupField`), a header already precedes
each group carrying that field's value (e.g. `─ URGENT (3) ─`, `─ IN PROGRESS (5) ─`).
The row then repeats the same field redundantly on every line under that header.

## Goal

When a `TaskField` is grouped on, drop that field's segment from the per-row layout:

| GroupField      | Row segment omitted            |
| --------------- | ------------------------------ |
| `Status`        | `[status]` badge               |
| `Priority`      | `[priority]` badge             |
| `List`          | `· {list}`                     |
| `Due`           | `· due {date}`                 |
| `Created`       | (no row representation → no-op) |
| `LastActivity`  | (no row representation → no-op) |

Omitting a badge must report the existing "absent" sentinel (`Start = -1`, `Length = 0`)
so `StatusBadgeListSource` draws no coloured span there.

## Scope (single phase — pure logic + threading + tests)

1. **`TaskRowFormatter.Format`** (`src/ClickUpTodo/Tui/TaskRowFormatter.cs`)
   - Add a trailing optional parameter `TaskField? groupedBy = null`
     (from `ClickUpTodo.Configuration`).
   - Skip the status badge when `groupedBy == Status` (return `(-1, 0)`); skip the
     priority badge when `groupedBy == Priority`; skip `· {list}` when `== List`;
     skip `· due` when `== Due`. `Created`/`LastActivity` and `null` change nothing.
   - Pure & unit-testable; the badge-span sentinel contract is unchanged.

2. **Thread `GroupField` through the render path** (`src/ClickUpTodo/Tui/TodoApp.cs`)
   - `BuildRow` and `AddTask` gain a `TaskField? groupedBy = null` parameter.
   - **To-do section only** passes `view.GroupField`. The pinned **Current Focus**
     section has no group headers, so its rows keep every segment (pass `null`).
   - `UpdateTaskRow` (in-place status update) rebuilds a row: a task appears in exactly
     one section (`nonPinned` excludes pinned), so use
     `groupedBy = _focus.IsPinned(id) ? null : _config.View.GroupField`.

3. **Tests** (`tests/ClickUpTodo.Tests/TaskRowFormatterTests.cs`)
   - Grouped by Status → no `[status]` badge (`Start=-1`, `Length=0`), other segments intact.
   - Grouped by Priority → no `[priority]` badge; status badge still exact.
   - Grouped by List → no `· {list}`; due still present.
   - Grouped by Due → no `· due`; list still present.
   - Grouped by Status with a priority set → priority badge still exact and now shifts
     left to sit right after the title (status omitted).
   - Grouped by Created / LastActivity / null → row unchanged (all segments present).

## Non-goals / notes

- No new setting, no `config.json` change, no F3 screen change — grouping already exists.
- Nesting (F4, `SubtaskArranger`) composes with grouping (#57) and is unaffected: only
  the row *text* changes, not which rows are grouped/nested. Depth-based indent still applies.
- Terminal.Gui rendering is unchanged (still driven by the reported badge spans); verify
  the visual result by build + reasoning per the repo's TUI rule.
