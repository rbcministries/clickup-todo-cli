# Plan: Priority field for F3 filter/sort/group (#48)

Add **Priority** to the F3 filter/sort/group engine, supporting all three operations. Priority is the
first **ordinal-with-labels** field (Urgent → High → Normal → Low), which fits neither the engine's
categorical nor its numeric/date path — so it needs a small, additive third field kind.

## Prerequisites already on `main`

- Field-generic F3 engine (#19 / PR #44): `TaskField`, `TaskFieldInfo`, `TaskView`, `FilterSortGroupForm`.
- **`priority` is already in the curated spec + generated `TaskObject`** (added by #33): the `Priority`
  schema exposes `id`, `priority` (name), `color`. **No Kiota regen / spec edit is needed** — the
  issue's "Data gap" section is stale.

## Design decisions

### Priority level model
ClickUp returns `priority` as `{ id, priority(name), color }` (or null). Its `id` is the canonical
level string: `1`=Urgent, `2`=High, `3`=Normal, `4`=Low (**lower = more urgent**). We derive
`PriorityLevel` (int 1–4) preferring `id`, falling back to a canonical **name→level** map; and a
normalized `PriorityName` (canonical Title-case, e.g. "Urgent"). Both are pure helpers on
`TaskFieldInfo` so the mapping has one source of truth and is unit-testable without the generated type.

### Third field kind: ordinal
`TaskFieldInfo.IsOrdinal(field)` alongside `IsNumeric`. Priority is ordinal:
- **Sort**: by level ascending → Urgent first (matches "Urgent → Low"); missing (no priority) always
  last via the existing `CompareNullableLast`. Descending → Low first, missing still last.
- **Group**: one bucket per priority, ordered by **level** (Urgent first), `(none)` last — *not*
  alphabetical (alpha would give High, Low, Normal, Urgent — wrong).
- **Filter**:
  - `IS` / `IS NOT` a priority name (or `(none)` / an unmatched value → the no-priority bucket).
  - Ordering ops compare **importance**: "higher priority than X" = more urgent = **smaller level
    number**. So `> Normal` ⇒ level < 3 (Urgent, High); `GEQ High` ⇒ level ≤ 2; etc. No-priority
    tasks never satisfy an ordering op. An unparseable target is a no-op (mirrors the numeric path).
  - `ValidOps(Priority)` = all six operators (priority is ordinal).

## Phases

1. **Model + mapping (+ tests)**
   - `TaskItem`: add `int? PriorityLevel`, `string? PriorityName`.
   - `TaskFieldInfo`: `PriorityLevelFromName`, `PriorityNameFromLevel`, `PriorityLevel(id, name)`,
     `PriorityNames`.
   - `ClickUpClient.Map`: populate the two new fields from `t.Priority`.
   - Tests: `TaskFieldInfoTests` for the mapping/round-trip.

2. **Engine extension (+ tests)**
   - `TaskField.Priority`; `TaskFieldInfo.IsOrdinal`, `DisplayName`, `ValidOps`, `OrdinalValue`.
   - `TaskView`: ordinal branch in `Matches` (IS/IS NOT + ordering-by-importance), `Compare`
     (level asc, nulls last), `Group`/`LabelFor` (label by name, order by level, `(none)` last).
   - Tests: `TaskViewTests` — sort Urgent→Low + nulls last, group order + `(none)` last, IS/IS NOT
     (incl. `(none)`), ordering ops semantics, null-priority handling.

3. **Dialog surface + validation (+ tests)**
   - `FilterSortGroupForm.Fields` gains `Priority`; `TryBuildRule` allows ordering ops on ordinal.
   - Value entry stays free-text (user types "Urgent"/"High"/…); the existing value label already
     says "name, or yyyy-mm-dd".
   - Tests: `FilterSortGroupFormTests` — Priority ordering rule accepted; round-trip includes Priority.

## Hard-rules compliance
- No hand-edits to `Generated/`; no spec change / no regen (priority already present).
- Personal-token auth untouched. TUI stays a single sectioned ListView (no new focusable pane — the
  dialog only gains a list item). All new logic in pure, unit-tested services.

## Out of scope / deferred
- Colouring priority labels (mirroring `StatusColor`) — not required by the acceptance criteria; can
  follow if wanted.
