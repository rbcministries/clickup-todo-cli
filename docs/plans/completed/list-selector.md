# Plan — #239: `ListSelectorView` + `ListSelectorModel` (K) — specialize the shared selector base for lists

## Goal (from the issue)

Build the List search-and-selector as a **thin specialization of the shared selector base** (#243,
now merged) — not a fork of the assignee selector — so the New Task screen (L, #240) and, adjacently,
Quick Updates (#242) reuse one implementation. Part of the #208 "Writing New Content" epic.

## Key discovery — the base already carries almost everything, and over the right id type

Reading the merged base:

- `Tui/SelectorModel.cs` is already keyed on **string** ids via `SelectorItem(string Id, string Name)`
  and `SelectorRow(string Id, string Name, bool Selected, bool Locked, bool Distinguished)`. It exposes
  `Format(row, distinguishedSuffix)`, `EmptyStateRows(selected, lockedIds, distinguishedIds, topFrequent,
  capacity)`, `SearchResultRows`, `Toggle`, `ShouldRunSearch`, and `ShouldPickFromSearchBox` — the last
  is the #234 footgun fix (Enter in an empty/search box never removes an already-selected row). All of
  this is already unit-tested in `SelectorModelTests.cs`, **including the distinguished/primary marker
  seam** (`Format_DistinguishedRow_AppendsSuffix_WhenSupplied`, `EmptyState_MarksDistinguishedSelected`,
  `EmptyState_MarksDistinguishedTopUp_WhenPrimaryIsUnselectedCandidate`).
- `Tui/SelectorView.cs` is the CI-untestable Terminal.Gui glue: single tab stop (`_list.TabStop =
  NoStop`), box↔list navigation, ~1s debounce + monotonic stamp coalescing, off-thread match via
  `Application.Invoke`, `✓` rendering with an injectable `distinguishedSuffix`, `Toggle`, both
  `CollectSelection`/`ImmediateApply` modes (optimistic add/remove + `Reconcile` + revert +
  out-of-order `_applyGeneration` guard), `Selection`/`SelectionChanged`/`Flash`, `_cts` lifecycle.
  It accepts a `distinguishedDefault` + `distinguishedSuffix` for the primary/home marker.
- The candidate pool already exists: `Services/ListFrequencyCache.cs` (#238, merged) exposes
  `Match(query, exclude)` and `TopMostFrequent(n, exclude)` over `NamedEntity(string Id, string Name)`
  (`ClickUp/Models.cs:90`).

Because lists are natively string-id'd, the list specialization is **thinner than the assignee one**:
the assignee side needs a `long ⇄ string` adapter at every boundary; the list side maps
`NamedEntity ⇄ SelectorItem` with no numeric parsing and no non-positive-id dropping.

So this issue supplies only the list-specific bits the issue enumerates:
- **Multi-select membership** — inherited from the base's `✓` multi-select; exposed as an ordered
  `Selection` (`IReadOnlyList<NamedEntity>`).
- **Primary/home list** — the base's distinguished marker hook. Tracked as `Selection[0]` (first
  seeded/selected), exposed as a `Primary` property, rendered with a `" (home)"` suffix. No undeletable
  lock — the "≥1 list" rule is the host's (L/#240) job, not this control's.
- Wire the base's `match`/`topFrequent` to `ListFrequencyCache.Match`/`TopMostFrequent`, and in
  `ImmediateApply` mode wire `applyAsync` to the host's list-membership write.

## Design decisions

### `ListSelectorModel` (pure) — a `NamedEntity`-typed façade over `SelectorModel`

Mirror `AssigneeSelectorModel` but over `NamedEntity` (string ids), so the list call sites and tests
stay in list terms while the shared logic lives in one place. A `ListRow(string Id, string Name, bool
Selected, bool Primary)` record is the list-worded row (assignees have `Locked`; lists have `Primary` —
the distinguished entry). The model adds only what the base can't express generically: the primary-row
marker. `Format(ListRow)` delegates to `SelectorModel.Format(row, " (home)")`. `EmptyStateRows`/
`SearchResultRows`/`Toggle`/`ShouldRunSearch` delegate to the base. No `Locked` concept on the list side
(the issue: "No undeletable lock").

### `ListSelectorView` — a `SelectorView` subclass over `NamedEntity`

Mirror `AssigneeSelectorView`: pass `NamedEntity ⇄ SelectorItem` adapters into the base ctor, seed the
primary as `distinguishedDefault` with `distinguishedSuffix: " (home)"`, and expose:
- `Selection` → `IReadOnlyList<NamedEntity>` (base `SelectedItems` mapped back).
- `Primary` → the distinguished entry if still selected, else `Selection[0]` if any, else `null`.
- List-worded flash messages for `ImmediateApply` failures ("Couldn't update lists: …").

Assignees drop non-positive ids at their boundary; lists only need the base's blank-id/name guard, so
the list adapters are identity-ish maps (`NamedEntity(Id, Name)` ⇄ `SelectorItem(Id, Name)`).

## Phases

1. **Model + tests** — add `Tui/ListSelectorModel.cs` (pure façade over `SelectorModel`, `ListRow`
   record, `" (home)"` primary suffix) and `tests/ClickUpTodo.Tests/ListSelectorModelTests.cs`
   mirroring `AssigneeSelectorModelTests` + the primary-marker cases and the #234 `ShouldPickFromSearchBox`
   cases. `dotnet test` green.
2. **View** — add `Tui/ListSelectorView.cs` (subclass of `SelectorView` over `NamedEntity`), exposing
   `Selection`/`Primary`, wired to `ListFrequencyCache.Match`/`TopMostFrequent`, both modes. Build
   green (0 warnings). TUI verified by build + reasoning + `tui-validate` where a host harness exists.

## Scope / deferred

- **No host wiring here.** The New Task List selector screen (L, #240) and the Quick Updates List pane
  (#242) are separate issues; this issue is only the reusable control + its pure model, exactly as #243
  delivered the base and #239's acceptance criteria scope it ("`ListSelectorModel` unit-tested;
  `tui-validate` exercises search → … in a host harness"). A standalone `tui-validate` host harness for
  the selector in isolation is adjacent harness-seeding work; if no such harness exists yet, note the
  manual verification in the PR and defer the dedicated scenario to the host-screen issue (#240).
