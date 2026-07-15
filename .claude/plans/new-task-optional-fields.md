# Plan — New Task optional fields: Due Date + Priority (#215)

Sub-issue **F** of the Writing New Content epic (#208). Adds the two optional "basic fields" to
the New Task compose screen (#213). **Tags are explicitly out of scope.**

## Ground truth (already on `main`)

- `NewTaskScreen` + pure `NewTaskForm` (#213) — the compose screen shell (Name / Description /
  Assignees). `NewTaskForm.TryBuild` validates and builds a `NewTaskRequest`.
- `NewTaskRequest` (#209) **already carries** `PriorityLevel` (int? 1=Urgent…4=Low) and
  `DueDateMs` (long? epoch ms). `ClickUpClient.CreateTaskAsync` already maps both onto the
  generated `CreateTaskRequest` (`Priority`, `DueDate`). **No API / spec / Kiota change needed.**
- `QuickUpdatesModel.PriorityLabels` (Urgent/High/Normal/Low + `(no priority)`) and
  `PriorityLevelForRow` / `NoPriorityRow` (#157) — the canonical priority row set to mirror.
- `TaskView.TryParseNumeric` (Services) — the app's one date→epoch-ms convention: `yyyy-MM-dd`
  (UTC midnight), raw epoch ms, or ISO date-time. Used by the F3 date filters; reuse it here.

So #215 is **pure form + TUI only**.

## Changes

1. **`NewTaskForm.TryBuild`** — add `int? priorityLevel` and `string? dueDate` params (before the
   `out`s). Blank due date → `DueDateMs = null`; else parse via `TaskView.TryParseNumeric`, and on
   failure return `false` with a new `DueDateInvalidError` (blocks Save). Normalise priority to the
   four canonical levels (else null). Name-required check stays first.
2. **`NewTaskScreen`** — add a Priority `ListView` (rows = `QuickUpdatesModel.PriorityLabels`,
   default-selected `NoPriorityRow`) and a Due-date `TextField`, anchored above the buttons; shrink
   the assignees selector (`Dim.Fill(9)`) to make room. Tab order:
   Name → Description → Assignees → Priority → Due Date → Save/Cancel (= `Add()` order). `TrySave`
   resolves the priority row → level (`PriorityLevelForRow`) and reads the due-date text, threads
   both into `TryBuild`; on a due-date error focus the due field, else the name field.
   No new keybinding (priority = ↑/↓ list, due = text); no second focusable pane on the **main
   list** (this is a modal screen).

## Tests

- **`NewTaskFormTests`** — adapt existing calls to the new signature (pass `null, null`); add:
  priority pass-through (1..4 set, 0/5/null cleared), valid `yyyy-MM-dd` → UTC-midnight epoch ms,
  raw epoch pass-through, blank/whitespace due → null, invalid due → `DueDateInvalidError`,
  combined name+priority+due.
- **`tui-validate` `new_task_check.py`** — update the tab count to reach Save past the two new
  fields; assert Priority rows (`Urgent`, `(no priority)`) and the Due-date label render, set a
  priority + a due date, and confirm Save still round-trips.

## Out of scope / deferred

- Tags (separate Epic). `NewTaskRequest` is already forward-compatible (#209).
- Server-side date-picker widgets / relative dates ("tomorrow") — plain documented format only.
