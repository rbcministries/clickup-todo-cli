# Plan — Make F4 a three-state subtask cycle (#179)

## Goal

Turn F4 from a boolean (subtasks nested-and-shown vs hidden) into a **three-state
cycle**, pressed repeatedly to advance:

1. **MineAndUnassigned** *(new default on-state)* — nested subtasks assigned **to me**
   or **unassigned**. Unassigned ones carry a trailing `(unassigned)` marker.
2. **All** — additionally include subtasks **not assigned to me** (today's
   `ShowAllSubtasksOfAssignedParents`/#70 behaviour, `(not assigned to you)` marker).
3. **Hidden** — the existing hidden state.

Cycle wraps `1 → 2 → 3 → 1`. Each press flashes the new state; state persists in
`ViewSettings`, superseding the `ShowSubtasks` + `ShowAllSubtasksOfAssignedParents`
boolean pair (with a config migration). Composes with the F12 "Show Completed" work
(#178/PR #190) — independent axes.

## Key domain insight

The existing #70 fetch (`TaskService.ResolveForeignSubtasksAsync`) already pulls **all**
subtasks of in-view parents regardless of assignee. A "foreign" (pulled-in, not in the
assigned snapshot) subtask is therefore either **unassigned** (no assignees) or
**assigned to others**. So the three states differ only by:

- **fetch gate:** pull the foreign set whenever `Subtasks != Hidden` (both on-states need
  it, to discover unassigned children) — previously gated on the #70 flag.
- **render filter:** state 1 shows only the *unassigned* foreign rows; state 2 shows all
  foreign rows. Toggling 1<->2 is a pure client-side re-render (the full set is already
  fetched); Hidden->on triggers a refresh (nothing fetched yet).

## Phases

### Phase 1 — domain + pure logic (unit-tested)
- **`ViewSettings`**: add `enum SubtaskView { Hidden, MineAndUnassigned, All }` and a
  persisted `SubtaskView Subtasks { get; set; } = Hidden`. Keep `ShowSubtasks`
  (`Subtasks != Hidden`) and `ShowAllSubtasksOfAssignedParents` (`Subtasks == All`) as
  `[JsonIgnore]` **read-only computed** getters so the many read sites keep compiling.
  Add legacy **deserialize-only** shims `LegacyShowSubtasks` / `LegacyShowAllSubtasks`
  (JSON `showSubtasks` / `showAllSubtasksOfAssignedParents`,
  `JsonIgnore(WhenWritingNull)`) for migration. `SubtaskViewExtensions.Next()` (cycle) +
  `Describe()` (flash/title text). Update `IsDefault` to gate on `Subtasks != Hidden`.
- **`ConfigMigrations`** v4: map legacy bools -> enum
  (`false`->Hidden, `true`+`!all`->MineAndUnassigned, `true`+`all`->All), null the shims.
  Bump `CurrentVersion` to 4.
- **`SubtaskVisibility`** (new, pure `Services`): `IsUnassigned(TaskItem)`;
  `IsVisibleForeign(TaskItem, SubtaskView)` (All->true; MineAndUnassigned->unassigned only;
  Hidden->false).
- **`TaskRowFormatter`**: add `UnassignedSubtaskMarker = "  · (unassigned)"` and an
  `isUnassignedSubtask` param (appended last so positional callers are unaffected);
  marker precedence context -> unassigned -> foreign(others).

### Phase 2 — TUI wiring (build + reason; tui-validate)
- **`TodoApp`**: F4 `ToggleShowSubtasks` -> `CycleSubtaskView` (advance/persist/flash;
  clear resolvers only when ->Hidden; refresh only when previous==Hidden). Foreign fetch
  gate -> `Subtasks != Hidden`. `Render` computes a state-filtered `VisibleForeignSubtasks`
  and uses it for the Focus list, the non-pinned concat, `suppressTopLevel`, and the
  per-row marker (unassigned vs others). `CandidateUniverse` uses the visible set.
  `CurrentSignature` folds the enum. `BuildFrameTitle` gains a subtasks flag.
  Simplify `ApplyViewSettings` (F3 no longer edits subtasks).
- **`FilterSortGroupScreen`**: remove the "pull children" button (superseded by F4
  state 2); preserve `Subtasks` in the built result.
- **Help**: `HelpScreen` F4 line describes the cycle; drop the stale "F3 can also nest…".

## Tests
- `ConfigMigrationsTests`: legacy bool->enum matrix, fresh install -> Hidden, idempotency,
  shim nulled + not re-persisted, on-disk load migration.
- `ViewSettingsConfigTests`: `Subtasks` round-trips as a string; `IsDefault` for each state.
- `SubtaskVisibilityTests`: `IsUnassigned` + `IsVisibleForeign` truth table.
- `SubtaskViewCycleTests`: `Next()` wraps 1->2->3->1 from Hidden.
- `TaskRowFormatterTests`: `(unassigned)` marker + precedence.

## Invariants
- No second focusable pane (#3); F4 stays a function-key list toggle.
- Bare letters reserved for type-ahead (#12).
- Generated client / curated spec untouched (no new API surface — assignees already on
  `TaskItem`).
- `#172`/PR #177 top-level suppression preserved (feeds the same `suppressTopLevel`).

## Deferred
- Making unassigned subtasks actionable (pin/status) — kept blocked like all foreign rows
  today; not in the issue's scope.
