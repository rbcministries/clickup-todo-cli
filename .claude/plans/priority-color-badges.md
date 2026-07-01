# Plan — Colour priority labels/badges (issue #55)

Follow-up from #48 (PR #54) / #16. Priority (#48) is filterable/sortable/groupable and mapped
onto `TaskItem` as `PriorityLevel` + `PriorityName`, but it is **never shown in the list row** and
carries **no colour**. #16 introduced a per-span colour overlay for the `[status]` badge. This issue
surfaces priority in the row as a **coloured `[priority]` badge**, mirroring the status badge.

## Acceptance criteria (from #55)

1. Add `PriorityColor` to `TaskItem`, mapped from `t.Priority?.Color` in `ClickUpClient.Map`,
   mirroring `StatusColor`.
2. Render a coloured priority indicator in the list row, reusing the existing badge-colouring
   mechanism from #16 so the single sectioned `ListView` model is preserved.
3. Keep the colour->attribute mapping in a pure, unit-tested helper (reuse `StatusBadgeColor` +
   `StatusBadgeListSource.TryCreate`, exactly as status does).

## Scope decision (why list-row, not the detail Other tab)

#55 says "a priority badge/label in the list row **and/or** the detail view's Other tab". The clause
"reusing the existing badge-colouring mechanism from #16 so the single sectioned `ListView` model is
preserved" only applies to the **list row** — that overlay mechanism (`StatusBadgeListSource`) is
list-specific. The detail Other tab is a plain, uncoloured `TextView` (its `Status:` line is
uncoloured too), so colouring it would need brand-new machinery and isn't "reusing #16". So the
coherent, in-spec slice is the **list row**; the detail tab already shows `Priority: {name}` and stays
as-is (uncoloured, consistent with `Status:` there). `PriorityColor` is added to `TaskItem`
only — not `TaskDetail` — to avoid dead, unrendered state.

## Design

The #16 overlay carries exactly **one** badge per row (`StatusBadgeListSource` holds a
`List<Badge?>` parallel to the display text). Priority is a **second** coloured span, so the minimal,
general change is: each row carries **0..N badges** instead of 0..1. `Badge` is already fully
self-describing (`Start`, `Length`, `Attr`), so `Render` just loops and overlays each — the spans
never overlap (status and priority are distinct segments).

### `TaskItem` + mapper
- `Models.cs`: add `string? PriorityColor { get; init; }` next to `PriorityName`.
- `ClickUpClient.Map`: `PriorityColor = t.Priority?.Color` (generated `Priority.Color`, already typed
  in the curated spec — **no spec/Kiota change needed**).

### `TaskRowFormatter` (pure, unit-tested)
- Layout becomes `{indent}{name}  [status]  [priority]  · {list}  · due {date}{marker}`.
- `Row` gains the priority span. Rename `BadgeStart`/`BadgeLength` -> `StatusStart`/`StatusLength` and
  add `PriorityStart`/`PriorityLength`. Offsets computed incrementally from the built string's length.
  Absent badge => `(Start=-1, Length=0)`, which `TryCreate` already treats as "no badge". The
  optimistic `(sending…)` suffix is appended after the body, so it never shifts a span.

### `StatusBadgeListSource` (generalise 1 to N badges)
- `IReadOnlyList<Badge?> _badges` -> `IReadOnlyList<IReadOnlyList<Badge>> _badges` (empty list =
  header/no badges). `Render` overlays each badge in the row's list. `TryCreate`, `OverlayBadge`,
  and delegated members unchanged. Per-badge base-attr capture/restore keeps the #34 bleed fix.

### `TodoApp` wiring
- `_badges` becomes `List<IReadOnlyList<Badge>>`. `AddHeader` pushes `[]`; `AddTask`/`UpdateTaskRow`
  push the row's badge list. `BuildRow` builds up to two badges (status via `StatusColor`, priority
  via `PriorityColor`), filtering the ones `TryCreate` rejects. No new focusable pane; type-ahead
  (#12) unaffected.

## Tests (xUnit, no UI)
`TaskRowFormatterTests` (extend, keep every assertion, migrated to `StatusStart`/`StatusLength`):
priority span exact; no-priority => length 0/start -1; priority + no status; indented shift; literal
`[High]` in title not confused. `StatusBadgeColorTests` (extend): ClickUp priority colours pick the
right foreground (Urgent red => light text; Low grey => dark text; High/Normal sampled).
The `IListDataSource` overlay needs a live driver => build-verified + reasoned (repo TUI rule).

## Manual TUI verification
`dotnet run --project src/ClickUpTodo`: a prioritised task shows a second coloured badge right after
`[status]` (bg = ClickUp priority colour, readable text); no-priority tasks show only `[status]`;
cursor/Tab/type-ahead unchanged.

## Phases
1. `TaskItem.PriorityColor` + mapper + pure `TaskRowFormatter` + unit tests -> build/test.
2. `StatusBadgeListSource` 1->N + `TodoApp` wiring -> build/test/format. Draft PR.
3. Quality gate, subagent review, finalize.
