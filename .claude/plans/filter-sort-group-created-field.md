# Plan: Filter / sort / group — add Created-date field — issue #43

## Goal
Add **Created date** (`date_created`) as a fourth date/numeric field to the F3
filter/sort/group engine, supporting filter (all six operators), sort, and group —
mirroring the existing **Last activity** / **Due date** fields.

## Context (why this is now unblocked & additive)
- Blocker **#33** merged: `date_created` is in the curated `clickup-openapi.json`
  `Task` schema, the generated `TaskObject.DateCreated` exists, and `MapDetail`
  already maps it onto `TaskDetail.CreatedMs`. **No spec edit / Kiota regen needed.**
- The field-generic F3 engine (**#19** / PR #44) is on `main`. `TaskField` is routed
  entirely through `TaskFieldInfo.IsNumeric` / `DisplayName` / `NumericValue`, so a new
  numeric/date field inherits `ValidOps` (all six operators), `Compare` (nulls-last),
  `LabelFor` (`yyyy-MM-dd` group labels), and `TryParseNumeric` value parsing for free.

## The one gap
`TaskItem` (the list-row record) has **no** `CreatedMs` — only `TaskDetail` does. The
list mapper `ClickUpClient.Map` doesn't set it. The F3 engine operates on `TaskItem`,
so we must surface `CreatedMs` there too.

## Changes (all within stable domain types — no generated code touched)
1. **`ClickUp/Models.cs`** — add `long? CreatedMs` to `TaskItem` (epoch ms, doc-commented
   like `UpdatedMs`).
2. **`ClickUp/ClickUpClient.cs`** — `Map`: `CreatedMs = ParseMs(t.DateCreated)` (already
   parsed identically in `MapDetail`).
3. **`Configuration/ViewSettings.cs`** — add `TaskField.Created` to the enum. Serialized as
   a **string** (`JsonStringEnumConverter`), so enum ordinal position is irrelevant to
   persisted configs; place it next to the other date fields for readability.
4. **`Services/TaskView.cs`** (`TaskFieldInfo`):
   - `IsNumeric` → `... or TaskField.Created`
   - `DisplayName` → `TaskField.Created => "Created"`
   - `NumericValue` → `TaskField.Created => task.CreatedMs`
   No other engine changes — `ValidOps`, `Compare`, `Group`, `LabelFor` all derive from these.
5. **`Tui/Screens/FilterSortGroupForm.cs`** — add `TaskField.Created` to the `Fields` list
   so it appears in the dialog's field dropdowns. Order: `Status, List, Created, LastActivity, Due`
   (group the date fields together; Created before LastActivity chronologically/logically).

## Tests (mirror the Last-activity / Due coverage; never weaken existing tests)
- **`TaskFieldInfoTests`**: `Created` is numeric (`IsNumeric` theory row); `ValidOps` =
  six operators; `DisplayName` = "Created".
- **`TaskViewTests`**: add a `created:` param to the `Task` helper; cover
  - filter: `GreaterOrEqual` / `LessThan` against a created date, null-excluded; `IsNot` keeps nulls;
  - sort: ascending oldest-first and descending newest-first, nulls always last;
  - group: buckets by UTC calendar day, `No date` bucket last.
- **`FilterSortGroupFormTests`**: `Created` is present in `Fields` / `FieldChoices`, and
  `FieldToIndex`/`IndexToField` round-trip it.
- **`ViewSettingsConfigTests`**: if it round-trips a `TaskField` through config, confirm
  `Created` survives (string enum).

## TUI note
No layout change — `Created` simply appears in the existing F3 dialog's field dropdowns.
The dialog already renders the field list from `FilterSortGroupForm.Fields`, so this is
data-only. Verified by build + the form unit tests; manual check: F3 → field dropdown lists
"Created", and selecting it sort/group/filters by creation date.

## Out of scope
Priority field (#48) — separate ordinal-with-labels engine extension + spec/regen.
