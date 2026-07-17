# Detail view: multi-list membership (locations) — issue #36

Follow-up from #17 / PR #33 (merged). The detail view's **Other attributes** tab shows the task's
**home list** (`list.name`). ClickUp's "Tasks in Multiple Lists" feature means a task can also belong
to additional lists, exposed on the task response's `locations` array, which the curated spec / detail
DTO don't yet surface. This adds that.

## Acceptance criteria (from the issue)

- Add `locations` to the curated `Task` schema (array of `{id, name}`) and regen Kiota.
- Surface them on `TaskDetail` (e.g. `Lists`), distinct from the home `ListName`.
- Render "Lists: a, b, c" in the Other tab when a task belongs to more than its home list.

## Design

`locations` is ClickUp's multi-list membership. Whether it includes the home list is not guaranteed
across API versions, so the presentation logic is written to be robust either way:

- **Spec:** add `locations` to the `Task` schema as an array of the existing `TaskList` component
  (`{id, name}`) — no new schema needed. All list endpoints are unaffected (the field is optional and
  absent from list responses).
- **Regen:** `dotnet kiota generate` (the `regen-client.ps1` body — `pwsh` is unavailable in this
  environment, so the underlying `dotnet kiota` command is invoked directly). Adds `Locations` to the
  generated `TaskObject`; no hand-edits to `Generated/`.
- **DTO:** add `IReadOnlyList<NamedEntity> Lists` to `TaskDetail`, defaulting to `[]`. This holds the
  raw `locations` membership faithfully (id + name), kept distinct from the home `ListId`/`ListName`.
- **Mapping:** `ClickUpClient.MapDetail` maps `t.Locations` → `Lists`, dropping entries with a blank
  name.
- **Rendering (pure, in `TaskDetailFormatter.OtherAttributes`):** compute the full membership as the
  home list unioned with `Lists`, de-duplicated by id (falling back to name when a location has no id),
  home-first. Render a new `Lists:` line **only when that membership has more than one entry** — so
  the line appears exactly when the task belongs to more than its home list, and the existing single
  `List:` line still covers the common single-list case.

## Phases

1. **Spec + regen + model + mapping.** Edit `clickup-openapi.json`; regen; add `TaskDetail.Lists`; map
   in `MapDetail`.
2. **Rendering + tests.** Add the `Lists:` line to `OtherAttributes`; extend `TaskDetailFormatterTests`
   (home + extras union & dedupe, locations-without-home, single-list omission, blank-name filtering).

## Out of scope

- Rich custom-field value rendering (#35) and the `sharing` array are not touched.
- No TUI wiring changes: `TaskDetailScreen` already renders `OtherAttributes(task)` verbatim, so the
  new line flows through with no screen changes.
