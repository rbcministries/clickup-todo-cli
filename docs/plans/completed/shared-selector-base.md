# Shared selector base — `SelectorView` + `SelectorModel` (#243)

Part of the **Writing New Content** epic (#208). Behavior-preserving refactor of the merged
`AssigneeSelectorView` / `AssigneeSelectorModel` (#212) into a reusable base so the List selector
(#239, K) can specialize it instead of forking a near-duplicate. **Blocks #239.**

## Decision: id-type strategy

**Standardize the base on `string` ids** (the issue's second option) rather than genericizing over
`TId`. Rationale: ClickUp list ids are strings; assignee ids are `long`. A single `string`-keyed base
keeps the base free of generic-type ceremony, and the only adaptation cost — `long.ToString()` /
`long.Parse` — is confined to the assignee boundary. `TaskAssignee` stays the assignee-facing type;
`SelectorItem(string Id, string Name)` is the base currency.

## Shared vs. specialized (verified against the current source)

Entity-agnostic → moves to the base:

- **View glue** (`AssigneeSelectorView`): `TextField` over `ListView` as a single tab stop
  (`_list.TabStop = NoStop`); `CursorDown`/`CursorUp` box↔list navigation; the debounce timer +
  monotonic `_searchStamp` coalescing (`ArmDebounce`/`OnDebounceFired`); off-thread `RunSearch`
  marshalled via `Application.Invoke`; `RenderEmptyState`/`SetRows`/`RenderCurrent`; `Pick` → toggle;
  ImmediateApply optimistic add/remove with the `_applyGeneration` out-of-order guard, `Reconcile`,
  and revert; `Selection`/`SelectionChanged`/`Flash`; `_cts` lifecycle/`Dispose`; the two modes.
- **Pure model** (`AssigneeSelectorModel`): `Format`, `EmptyStateRows`, `SearchResultRows`, `Toggle`,
  `ShouldRunSearch`, and the row record.

Differs:

- **Id type:** `long` (assignees) vs `string` (lists) → base is `string`; assignee adapts.
- **Item type:** `TaskAssignee(long,string)` vs `NamedEntity(string,string)` → base `SelectorItem`.
- **Distinguished item:** assignee `lockedDefault` (non-removable current user) vs list **primary/home**
  (the create target). Generalized to "a distinguished, optionally-locked selected entry + a marker
  hook in `Format`." Assignees keep `Locked` with **no visible marker** (byte-identical today);
  lists get a `Distinguished` flag + injectable marker suffix.

## Target shape

- **`Tui/SelectorModel.cs`** (new, pure): `record struct SelectorItem(string Id, string Name)`;
  `record struct SelectorRow(string Id, string Name, bool Selected, bool Locked, bool Distinguished)`;
  `enum ToggleKind` (moved here — shared); `record struct SelectorToggle(ToggleKind Kind, string Id)`;
  `static class SelectorModel` with `Format(row, distinguishedSuffix = "")`, `EmptyStateRows`,
  `SearchResultRows`, `Toggle`, `ShouldRunSearch`. Drops blank ids / blank names; de-dupes by id.
- **`Tui/SelectorView.cs`** (new, base `View`, unsealed): all the machinery over `SelectorItem`,
  callbacks in `SelectorItem` terms, `enum SelectorMode { CollectSelection, ImmediateApply }`,
  `lockedDefault` + `distinguishedDefault` seeds, `distinguishedSuffix` render hook, `SelectedItems`,
  `SelectionChanged`, `Flash`.
- **`Tui/AssigneeSelectorView.cs`** → `sealed class AssigneeSelectorView : SelectorView`: same public
  ctor (adapts `TaskAssignee`/`long` callbacks to `SelectorItem`/`string` for `base(...)`), and a
  `Selection` property mapping `SelectedItems` back to `IReadOnlyList<TaskAssignee>`.
- **`Tui/AssigneeSelectorModel.cs`** → typed façade over `SelectorModel`. Keeps `AssigneeRow(long …)`
  and `ToggleResult(ToggleKind, long)`; methods delegate (filtering non-positive ids at the boundary
  so the existing tests stay green). No logic duplicated.
- Consumers (`NewTaskScreen`, `QuickUpdatesScreen`) move from `AssigneeSelectorMode` to the single
  `SelectorMode` enum — a mechanical reference update, behavior-identical.

## Phases

1. **Pure layer.** Add `SelectorModel.cs`; refactor `AssigneeSelectorModel` to delegate; add
   `SelectorModelTests.cs` (generic behavior incl. distinguished marker + string ids).
   `AssigneeSelectorModelTests` stay green. build + test.
2. **View layer.** Add `SelectorView.cs`; refactor `AssigneeSelectorView` onto it; consolidate the
   mode enum; update the two call sites. build + test.
3. **Validate + finalize.** `dotnet format`; `tui-validate` parity on the Quick Updates assignee flow
   (byte-identical renders where the harness asserts); PR ready.

## Acceptance criteria (from #243)

- One base powers the assignee selector with **no behavior change**: `AssigneeSelectorModelTests` /
  `AssigneeFrequency*` stay green; `tui-validate` shows parity on the Quick Updates assignee flow.
- The base exposes the seams #239 needs: multi-select `✓`, a distinguished/primary marker, string
  ids, both `CollectSelection`/`ImmediateApply` modes.
- `dotnet test` green (integration `SkippableFact`, env-gated), then `tui-validate` parity.

## Invariants

- No `Generated/` edits, no spec change (pure TUI refactor). Personal-token raw `Authorization` header
  untouched. Single focusable composite preserved (`_list.TabStop = NoStop`) — no second focusable
  pane (#3). Chord/function-key model untouched.
