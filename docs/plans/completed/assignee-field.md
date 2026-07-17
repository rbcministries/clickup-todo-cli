# Plan — #68: Assignee as an F3 filter/sort/group field (multi-valued), defaulting to the app user

## Goal (from the issue)

Make **Assignee** a first-class F3 view field, defaulting to the app's user, and use it to drive the
task fetch instead of the hard-coded server-side "assigned to me" query. Foundational for #69
(migrate status exclusions to filter rules) and #70 (show a parent's subtasks regardless of assignee).

## Key discovery — no spec edit / Kiota regen needed

The issue's "add `assignees` to the Task schema, then regenerate" step is **already done**: the shared
`Task` component schema in `clickup-openapi.json` already has `assignees: [User]`, and the generated
`TaskObject.Assignees` (`List<User>` with `Id`/`Username`/`Email`) already exists (landed with the #17
detail view — `MapDetail` reads `t.Assignees`). So this change **does not touch `Generated/` or the
spec** — the "never hand-edit generated code" rule is satisfied by not going near it.

## Design decisions

### Assignee is a **fetch-layer** filter, not a client-side re-filter

Per the issue's decided model ("keep the assignee constraint server-side and re-fetch when the rule
changes ... apply the *rest* of the F3 view client-side"):

- An `Assignee IS <me|id>` rule contributes to the **server-side** `assignees[]` query parameter.
- The rule is **not** applied as a client-side `TaskView` filter (client-side `Matches` treats an
  Assignee rule as a no-op). This is deliberate: it (a) preserves today's behaviour exactly for the
  default and (b) avoids a regression where the default `Assignee IS me` would otherwise drop
  tasks on the **Personal Tasks list that are assigned to someone else** (those are unconditionally
  merged today, regardless of assignee).
- Editing the Assignee rule changes *what is fetched*, so it triggers a **reload**, not just a
  client-side re-render (unlike Status/List/Due/Priority rules).

### Group / sort by Assignee = client-side, first-assignee bucket

`CategoricalValue(task, Assignee)` returns the **first** assignee's display name (issue's recommended
"least surprising" single bucket), so grouping/sorting reuse the existing categorical machinery with
no new code paths. A task with no assignees falls in the `(none)` bucket (last), like other
categorical fields.

### Default seeding + one-shot migration (the pattern #69 will reuse)

- Seed the default view with a single rule: `Assignee IS me` (value = the literal token `"me"`, not a
  numeric id — so no user id is needed at config-load time; `"me"` is resolved to the app user id only
  at the fetch layer, which has the id).
- `AppConfig.SchemaVersion` (new, default `0`) gates a one-shot migration in `ConfigStore.Load` →
  `ConfigMigrations.Apply`: at v0→v1, insert the `Assignee IS me` rule **iff** the view has no Assignee
  rule, then stamp `SchemaVersion = 1`. Version-gating (not "seed whenever absent") is what lets a user
  who *intentionally* clears the assignee rule (→ "everyone") keep that choice across restarts.
- `ViewSettings.IsDefault` now means *exactly* the single seeded `Assignee IS me` rule and nothing
  else (no other filters, no sort/group/subtasks) — the issue explicitly requires this.

### Value grammar for this slice: `me` / numeric id only

`ValidOps(Assignee) = [IS]` for now. Resolving arbitrary usernames/emails → ids needs a workspace
members lookup; `IS NOT` and client-side multi-assignee any/none matching are entangled with the
client/server split. All three are **deferred to a linked follow-up issue** (the issue permits
"restrict the value to ids/'me' initially"). `(none)`-assignee filtering likewise defers.

## Phases

1. **Model + fetch layer** — `TaskAssignee` record + `TaskItem.Assignees`; map `t.Assignees` in
   `ClickUpClient.Map` (make `Map` internal for a unit test); generalize `GetAssignedTasksAsync` to
   take an `IReadOnlyList<long>` assignee-id set (empty ⇒ workspace-wide "everyone"). Unit tests for
   mapping + the query shape.
2. **Config: field, seeding, migration** — `TaskField.Assignee`; `ViewSettings.CurrentUserToken`,
   `DefaultAssigneeRule()`, new `IsDefault`; `AppConfig.SchemaVersion`; `ConfigMigrations`; wire into
   `ConfigStore.Load`. Update/extend config tests; new `ConfigMigrationsTests`.
3. **Engine + service resolution** — `TaskFieldInfo` (`DisplayName`, `ValidOps`, `CategoricalValue`);
   `TaskView.Matches` Assignee no-op branch; `TaskService.ResolveAssigneeIds` (+ instance wrapper,
   `SameAssigneeSet`) and `LoadAsync` using it. Unit tests for resolution + engine (no-op filter,
   group/sort by assignee).
4. **F3 UI + reload wiring** — `FilterSortGroupForm.Fields` + `TryBuildRule` via `ValidOps`;
   `FilterSortGroupScreen` "Clear all" resets to default (me-rule) not empty; `TodoApp.ApplyViewSettings`
   reloads when the resolved assignee set changes (with a "may be slow" flash when it becomes empty).
   Build + form tests; TUI verified by reasoning (documented in PR).

## Deferred (→ follow-up issue, linked from the PR)

- Resolve arbitrary usernames/emails → assignee ids via a workspace-members lookup.
- `Assignee IS NOT` + client-side multi-assignee any/none `Matches`; `(none)`/unassigned bucket as a
  filter value.
- "Everyone" (empty assignee set) broad-fetch UX polish (paging cost warning / confirmation).
