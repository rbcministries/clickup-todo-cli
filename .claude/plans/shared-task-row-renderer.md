# Shared task-row / badge render component (#284)

**Issue:** #284 (Mouse/UX P) — *factor out a shared task-row / badge render component for
reuse by the main list and the Task Tree tab.* Part of epic #283; foundation that blocks F
(#291, Task Tree tab).

**Nature:** pure, behaviour-preserving refactor. No user-visible change. No new ClickUp API
surface, no keybindings, no second focusable pane.

## Problem

Producing a rendered list row today takes **two** calls in two places:

1. `Tui/TaskRowFormatter.Format` (pure) → a `Row` = the display text plus the char spans of the
   leading custom-id chip, the Status/Priority badges, and the trailing Assignees badge.
2. `TodoApp.BuildRow` (private static) → pairs each span with the *right hex colour*
   (Status→`task.StatusColor`, Priority→`task.PriorityColor`, custom-id→a fixed muted gray,
   Assignees→a fixed white) via `StatusBadgeListSource.TryCreate`, yielding
   `(string Text, IReadOnlyList<StatusBadgeListSource.Badge> Badges)` — exactly what
   `StatusBadgeListSource` consumes.

Step 2 — the "which colour goes on which span" knowledge, plus the two fixed colour
constants (`AssigneesBadgeColor`, `CustomIdBadgeColor`) — lives *inside `TodoApp`*. A second
consumer (the Task Tree tab, F/#291) would have to reach into `TodoApp` or fork the logic to
render a row identically. That is the fork this issue exists to prevent.

## Approach

Introduce `Tui/TaskRowRenderer` — a pure static component that folds steps 1 + 2 into one
call a non-host caller can make over an arbitrary `TaskItem` (no dependency on `TodoApp`'s
`_rows`/`_display` row arrays):

```csharp
public static class TaskRowRenderer
{
    public const string AssigneesBadgeColor = "ffffff"; // moved verbatim from TodoApp
    public const string CustomIdBadgeColor  = "5a5a5a"; // moved verbatim from TodoApp

    public readonly record struct RenderedRow(
        string Text, IReadOnlyList<StatusBadgeListSource.Badge> Badges);

    public static RenderedRow Render(
        TaskItem task, BadgeDisplay badgeDisplay, long? currentUserId, int depth = 0,
        bool isContextParent = false, TaskField? groupedBy = null, string marker = "",
        bool isForeignSubtask = false, bool isUnassignedSubtask = false);
}
```

The `Render` body is **byte-for-byte** the current `TodoApp.BuildRow` body: call
`TaskRowFormatter.Format` with the same argument order, then add the same four
`StatusBadgeListSource.TryCreate` badges (Status, Priority, custom-id, Assignees) in the same
order with the same colours. `RenderedRow` is a positional `record struct`, so it
auto-deconstructs to `(text, badges)` and the existing call sites read unchanged.

Notes on faithfulness:

- `currentUserId` widens from `long` (BuildRow) to `long?` so a standalone caller need not
  invent a user id; `TaskRowFormatter.Format` already takes `long?`, and `TodoApp` still passes
  its non-null `_tasks.UserId`, so the value flowing through is identical → no behaviour change.
- `StatusBadgeListSource.Badge`, `.TryCreate`, `.HeaderAttr`, and the colour math stay put —
  they are the data source's contract; the renderer *calls* `TryCreate` exactly as `BuildRow`
  did. This keeps the well-tested colour/overlay code untouched.
- The main list's row-building path (`TodoApp.AddTask` and the in-place `UpdateTaskRow`) now
  calls `TaskRowRenderer.Render`; `TodoApp.BuildRow` and its two colour constants are removed.
  `FoldMarker` stays in `TodoApp` (it is host fold-state plumbing) and is passed as the
  `marker` string, exactly as today.

## Scope boundaries

- **Not** building the Task Tree tab (that is F/#291) — only making the row render reusable.
- `StatusBadgeListSource` still receives pre-built `_display`/`_badges` from the host; the
  refactor changes *where the (text, badges) pair is produced*, not the source's role.
- F6 badge-mode plumbing (`Configuration/BadgeDisplay.cs`) is unchanged — the mode is an
  argument, so a future consumer can request a fixed mode without a toggle.

## Acceptance criteria (from #284)

- [ ] Main list renders identically before/after — rows, badge modes, colours, type-ahead
  keys, indentation, fold glyphs — verified by `tui-validate` A/B parity, no regression.
- [ ] The component is consumable standalone by a non-host caller: a unit test constructs it
  and asserts row text + colour spans for a small ancestry set (parent + depth-1 child)
  without spinning up `TodoApp`.
- [ ] `dotnet build` 0/0, `dotnet test` green, `dotnet format` clean.

## Test plan

New `tests/ClickUpTodo.Tests/TaskRowRendererTests.cs`:

- **Delegation parity** — for a representative task in each `BadgeDisplay` mode, the rendered
  `Text` equals `TaskRowFormatter.Format(...).Text`, and each emitted `Badge` covers exactly the
  span `Format` reported (Status/Priority/custom-id/Assignees), proving the renderer wires the
  same spans the formatter produces.
- **Colour mapping** — Status badge carries the status colour, Priority the priority colour,
  custom-id the fixed gray, Assignees the fixed white (assert the resulting `Attribute`).
- **Absent/hidden spans** — a task with no priority / hidden badges emits no badge for the
  missing spans (mirrors `TryCreate`'s `(-1,0)` sentinel), so nothing is over-shaded.
- **Standalone ancestry set** — build a parent (depth 0) + child (depth 1) purely as
  `TaskItem`s, render each, and assert indentation + spans hold with no `TodoApp` present
  (this is the #284 "consumable by a non-host caller" criterion).

TUI: verified by build + `tui-validate` A/B parity against the stock renderer (no rendering
change is expected — the produced strings and badge spans are identical to pre-refactor).
