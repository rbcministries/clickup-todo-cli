# Migrate excluded-statuses setting to F3 filter rules (#69)

Retire the standalone **excluded-statuses** mechanism (`AppConfig.ExcludedStatuses` +
`TaskService.ExcludeByStatus` + the F2 Settings section) and express it as ordinary F3 filter
rules (`Status IS NOT <name>`), so visibility is decided in exactly one place: `TaskView.Filter`.
Existing users' saved exclusions migrate **automatically, one-shot, with no user intervention**.

Sibling of #68 (Assignee field) — reuses the same `SchemaVersion`-gated seed/migrate pattern in
`ConfigMigrations`.

## Key decision: fresh install seeds the default exclusions

The acceptance criteria call for migration-mapping tests covering the **"empty/absent legacy
field"** cases — i.e. empty and absent map *differently*:

- **Absent** legacy field (a fresh install, or a config that never had the key) → **seed the
  default exclusions** (`won't do`, `cancelled`). This preserves today's default behaviour (those
  statuses have always been hidden by default) under the new single-mechanism model. Without this,
  brand-new installs would suddenly show `won't do`/`cancelled` — a silent regression.
- **Empty** legacy array `[]` (a user who deliberately cleared their exclusions) → **seed nothing**.
- **Present** non-empty (`["qa","won't do"]`) → migrate each to a `Status IS NOT` rule.

Because every existing user's `config.json` currently carries `excludedStatuses` (it's a live
property with a non-null default, always serialized), existing users always go down the
"present → migrate" path; only genuinely fresh installs hit "absent → seed defaults". Both paths
converge on the same untouched-install rule set, so old and new users are consistent.

`ViewSettings.IsDefault` is updated so an untouched install — `Assignee IS me` **plus** the two
default `Status IS NOT` rules — still reads as **default** (per the issue's "confirm the interaction
with #68's seeded default assignee rule" note).

## Phase 1 — model, migration, filter (the core; fully unit-testable)

1. **`AppConfig`**: turn `ExcludedStatuses` into a **deserialize-only shim**.
   - Rename to `LegacyExcludedStatuses`, `[JsonPropertyName("excludedStatuses")]`, type
     `List<string>?`, default `null`, `[JsonIgnore(Condition = WhenWritingNull)]`.
   - Absent key → `null` (distinguishable from empty `[]`); migration nulls it after running so
     it's never re-written (one-shot; subsequent loads see no legacy field).
2. **`ViewSettings`**:
   - `public static readonly IReadOnlyList<string> DefaultExcludedStatuses = ["won't do","cancelled"];`
   - `public static FilterRule StatusIsNotRule(string status)` factory.
   - Rewrite `IsDefault`: no sort/group/subtasks **and** the filter set is exactly
     {`Assignee IS me`} ∪ {`Status IS NOT s`} for each default excluded status (order-independent).
3. **`ConfigMigrations`**:
   - Bump `CurrentVersion` to `2`.
   - `if (config.SchemaVersion < 2) MigrateStatusExclusions(config)`:
     `toExclude = LegacyExcludedStatuses ?? DefaultExcludedStatuses`; for each, append a
     `Status IS NOT` rule **if not already covered** (case-insensitive, so re-running / a config
     already carrying the rule doesn't duplicate); then set `LegacyExcludedStatuses = null`.
4. **`TaskService`**: drop the `ExcludeByStatus` call from `LoadAsync` (visibility is solely
   `TaskView.Filter`, applied by the caller) and remove the now-unused `ExcludeByStatus` method.

**Tests (Phase 1):**
- `ConfigMigrationsTests`: fresh config now seeds assignee + 2 status rules and is `IsDefault`;
  absent->defaults, empty->none, present->mapped, de-dup, case-insensitive; idempotent; already-current
  version untouched; on-disk legacy load drops the `excludedStatuses` key on save.
- `ViewSettingsConfigTests`: update `IsDefault` expectations for the new default set.
- Remove `StatusExclusionTests` (its method is gone); the equivalent behaviour — `Status IS NOT`
  excludes matches case-insensitively and keeps blank/null status — is already covered by
  `TaskViewTests.Filter_CategoricalIsNot_ExcludesMatches_AndKeepsNulls`.

## Phase 2 — remove the F2 Settings exclusions UI (TUI; build-verified)

- `SettingsScreen`: remove the "Excluded statuses" label/list/add/remove controls and the
  `excludedStatuses` ctor parameter; keep the refresh + agent-dispatch layout.
- `SettingsResult`: drop `ExcludedStatuses`.
- `TodoApp.OpenSettings`: stop passing/reading `ExcludedStatuses`; update the Flash text.
- `SettingsForm`: remove the now-unused `CanAdd` helper.
- `SettingsFormTests`: remove the `CanAdd_*` tests (helper removed); keep refresh/extra-args tests.

TUI isn't unit-testable in CI — verify by building 0/0 and reasoning; describe the before/after in
the PR. No second focusable pane is introduced (the screen keeps its existing layout).

## Acceptance mapping

- Upgrading user with `excludedStatuses:["qa","won't do"]` -> sees them as visible F3 `Status IS NOT`
  rules; F2 no longer shows an exclusions section. (migration + UI removal)
- `excludedStatuses` key gone from `config.json` after first run. (shim nulled; persisted on next
  Save - same lazy-persist as #68; fresh installs never write it)
- No task filtered by any mechanism other than `TaskView.Filter`. (`ExcludeByStatus` removed)
- Unit tests for migration mapping (de-dup, case-insensitive, empty/absent) and that `LoadAsync` no
  longer excludes by status.

## Out of scope / deferred

None specific to this issue. Multi-assignee semantics remain in #73; subtasks-regardless-of-assignee
in #70 (which this unblocks by giving it a single inspectable filter model).
