# Plan: Filter / sort / group dialog (F3) — issue #19

## Goal
Add an **F3** dialog to filter, sort, and group the task list, persisting the active
view in `config.json` so it survives restarts. Keep the single sectioned `ListView`
model (#3) — groups become header rows in the existing `_rows` mechanism.

## Scope of this slice (conflict-avoiding)
Fields available **without** a curated-spec change / Kiota regen:

| Field          | Source on `TaskItem`        | Kind        | Notes |
| -------------- | --------------------------- | ----------- | ----- |
| Status         | `StatusName`                | categorical | IS / IS NOT |
| List           | `ListName`                  | categorical | IS / IS NOT |
| Last activity  | `UpdatedMs` (**new**)       | date/numeric| from spec's existing `date_updated`, just mapped onto `TaskItem` |
| Due date       | `DueDateMs` (existing)      | date/numeric| IS/IS NOT/>/</GEQ/LEQ |

**Deferred to a follow-up issue** (filed + linked from the PR):
- **Created date** (`date_created`) as a field — needs a `clickup-openapi.json` edit +
  Kiota regen, which collides head-on with in-flight PR #33 (also adds `date_created`).
  Add the field cleanly after #33 merges.

## Filter operators
- `IS`, `IS NOT` — all fields.
- `>`, `<`, `GEQ`, `LEQ` — numeric/date fields only (Last activity, Due). Categorical
  fields (Status, List) are limited to IS / IS NOT.
- Multiple rules are ANDed.

## Sort
- By any field, asc/desc. Default (no sort configured) = today's order: due date
  (soonest first, undated last), then name — `TaskService.TaskOrder` behaviour preserved.
- Nulls sort last regardless of direction.

## Group
- Group by a field; each group renders under a header row reusing the `null`-entry rows
  in `_rows` (same mechanism as the pinned/main split). `Tab` already jumps headers.
- Categorical group key = the field value, or a `(none)` bucket last.
- Date group key = the UTC calendar day (`yyyy-MM-dd`), or `No date` last. Groups
  ordered chronologically; the `(none)`/`No date` bucket is always last.
- Within a group, tasks follow the configured sort (or the default).

## Pinned interaction
- The **Current Focus** (pinned) section is shown as today, unaffected by filters/groups
  (explicit pins shouldn't vanish). It is sorted by the configured sort for consistency.
- Filter + group apply to the **non-pinned** set only.

## Architecture
Pure, unit-tested engine separated from presentation so the de-modal screen migration
in #38 is a later presentation-only change.

- `Configuration/ViewSettings.cs` — persisted view: `TaskField`, `FilterOp`,
  `SortDirection` enums; `FilterRule(Field, Op, Value)` record; `ViewSettings`
  (`Filters`, `SortField?`, `SortDirection`, `GroupField?`).
- `AppConfig.View` — persisted; `ConfigStore` JSON options gain `JsonStringEnumConverter`
  so enums round-trip as strings.
- `Services/TaskView.cs` — `Filter`, `Sort`, `Group`, and `Apply(tasks, settings)` →
  `IReadOnlyList<TaskGroup>` (`TaskGroup(Label?, Tasks)`). `TaskFieldInfo` helpers
  (kind, display name, value accessors, valid ops, value parsing).
- `ClickUpClient.Map` — map `date_updated` → `TaskItem.UpdatedMs`.
- `Tui/FilterSortGroupDialog.cs` — F3 modal (RadioGroup + ListView + TextField + Buttons),
  returns a `ViewSettings` or null. All parsing/validity in testable helpers.
- `Tui/TodoApp.cs` — F3 handler; `Render` consumes `TaskView.Apply`; help + hint updated.

## Phases
1. **Engine + data + config** (pure, fully unit-tested): `UpdatedMs` mapping,
   `ViewSettings`, `TaskView`/`TaskFieldInfo`, `ConfigStore` enum converter. Tests.
   → first push, draft PR.
2. **TUI**: F3 dialog + `TodoApp` wiring (grouped render, help/hint). Build + format.
3. **Finalize**: review subagent, address findings, mark ready.

## Tests
- `TaskViewTests` — filter (each op, categorical vs numeric, null handling, AND of rules),
  sort (each field, asc/desc, nulls-last, default), group (categorical, date-day, none
  bucket ordering, ungrouped).
- `ViewSettingsConfigTests` — config round-trips `View` (enums as strings) through
  `ConfigStore`.
- `TaskFieldInfoTests` — valid ops per field, value parsing (date string → ms).

## Verification
- `dotnet build -c Release` (0/0), `dotnet test -c Release`, `dotnet format`.
- TUI not runnable in CI: describe manual F3 verification in the PR (open F3, add a
  Status IS filter, sort by Last activity desc, group by List, Save → list re-renders
  grouped; Esc preserves cursor; reopen app → view persisted).
