# `@`-mention picker — `MentionPickerView` + `MentionPickerModel` (#324)

Part of the "Task Detail & Comments UX" epic (#313), sub-issue **J**. Build a modal
search-and-select for choosing a workspace member to `@`-mention, **reusing** the shared
`SelectorView`/`SelectorModel` base (#243) rather than forking a near-duplicate — the same
specialization pattern `AssigneeSelectorView` (#212) and `ListSelectorView` (#239) already follow.

Consumed by **K** (comment composer — #325) and **L** (description editor — #326); those are the
issues that host the picker in a live screen. This slice ships the reusable component, its pure
companion, and unit tests.

## Dependencies (all landed on `main`)

- **I — member display-name + name→userId resolution (#323).** Provides
  `WorkspaceMember.DisplayName` (a computed, **guaranteed-non-blank** name: ClickUp's spaced
  username, else the email local part, else `User {id}`) and `Services/MemberResolver` (exact,
  trimmed, case-insensitive name→id). The picker rows display `DisplayName` and key on the
  numeric `Id`, so a chosen member submits by **userId**, never the raw typed text — the reason
  the epic gated J on I (spaced names like "Ben Seymour" would otherwise not round-trip).
- **Shared selector base (#243).** `Tui/SelectorView.cs` (`class SelectorView : View`) + pure
  `Tui/SelectorModel.cs`, keyed on **string** ids, with debounced type-ahead, the add/remove
  toggle, and the `#234` search-box-is-add-only discipline. `AssigneeSelectorView` /
  `ListSelectorView` are the existing thin adapters — the intended reuse.
- **Models:** `WorkspaceMember(long Id, string? Username, string? Email)` with the computed
  `DisplayName` (#323).

No curated-spec / Kiota / `Generated/` change, no new ClickUp API surface, no bare-letter
keybinding, no second focusable pane (this is a modal-screen composite, #3/#38).

## Scope decision — foundation slice, consumers deferred

The epic wires the picker into two authoring surfaces, both of which are their own open issues:

- **Comment composer → #325 (K)** — "wire the @-mention picker into the comment composer (Ctrl+N)".
- **Description editor → #326 (L)** — "@-mention in the description editor (Ctrl+E)"; also
  spike-gated on **G** (#321).

Neither host exists yet, so this slice delivers the **component + pure companion + unit tests**,
exactly the cut the epic's other foundation pieces took (e.g. `AssigneeSelectorView` #212 shipped
the reusable view with no live consumer in the slice — PR for #212). The picker has no invocation
surface until K/L, so an interactive `tui-validate` pass is deferred to those issues and noted in
the PR; this slice validates the logic through the pure `MentionPickerModel`, per the repo's
`DetailPaneView`/`SelectorModel` split convention. `dotnet build` proves the view compiles.

### `@Brain` / named Super Agents — omitted for now

#324 asks to include `@Brain` / Super Agents as selectable mention targets **iff G (#321)** found
them API-addressable. #321 is an unfinished spike, so per this issue's acceptance criteria they are
**omitted** here and this is noted; revisit when #321 lands (the candidate pool is injected as a
delegate, so adding a synthetic entry later needs no change to the picker itself).

## Design — pure companion + thin View (the repo split)

### 1. Pure logic — `Tui/MentionPickerModel.cs`

A **member-typed façade** over `SelectorModel`, mirroring `AssigneeSelectorModel`/`ListSelectorModel`.
Keeps the mention boundary in `WorkspaceMember` / `long` userId, adapting to the base's string-id
`SelectorItem`. Records:

- `MentionTarget(long UserId, string DisplayName)` — what a pick yields, for K/L to turn into a
  mention token/block.
- `MemberRow(long Id, string Name, bool Selected)` — one rendered row (no `Locked`/`Distinguished`:
  the picker opens empty and is single-select in practice).
- `MemberToggleResult(ToggleKind Kind, long Id)`.

Methods (all delegating to `SelectorModel`, so no forked logic):

- `Format(MemberRow)` → `"✓ {Name}"` / `"  {Name}"` (no distinguished suffix).
- `EmptyStateRows(selected, topFrequent, capacity)` — chosen members first (in practice none),
  then the ranked candidate pool up to `capacity`. Empty locked/distinguished sets.
- `SearchResultRows(matches, selectedIds)` — matches as unselected rows, excluding already-chosen.
- `Toggle(selectedIds, id)` → `Added`/`Removed` (mentions carry no locked entry, so never
  `LockedNoOp`).
- `ShouldRunSearch(capturedStamp, currentStamp)` — debounce coalescing.
- Conversions (internal): `ToItem(WorkspaceMember)` → `SelectorItem(Id.ToString(), DisplayName)`
  (drops `Id <= 0`); `ToTarget(SelectorItem)`/`ToTarget(WorkspaceMember)` → `MentionTarget`
  (parses the id — **never the typed text** — so spaced names submit correctly).

### 2. Thin View — `Tui/MentionPickerView.cs`

`sealed class MentionPickerView : SelectorView`. Ctor takes injected candidate delegates over
`WorkspaceMember` (`match`, `topFrequent` — a member roster, optionally ranked via the
assignee-frequency plumbing #155 at the call site), `timeProvider`/`debounce`/`capacity` seams.
Constructs the base in `CollectSelection` mode with **no** `initialSelected`/`lockedDefault`/
`distinguishedDefault` (a picker seeds nothing). Adapts `WorkspaceMember` ⇄ `SelectorItem` via the
model's conversions, dropping non-positive ids at the boundary (mirrors `AssigneeSelectorView`).

Surfaces the pick as `event EventHandler<MentionTarget> MemberPicked`, raised from the base's
`SelectionChanged` by diffing `SelectedItems` against an announced-id set — so a pick fires exactly
once and a host that closes on the first pick gets one `MentionTarget`. All layout/input/debounce
stays in the base: still a single focusable composite (no second focusable pane), embeddable in a
modal screen that owns Tab/Esc, consistent with how the assignee selector is hosted.

## Tests — `tests/ClickUpTodo.Tests/MentionPickerModelTests.cs`

Mirror `AssigneeSelectorModelTests`/`SelectorModelTests`:

- `Format` — selected/unselected prefixes.
- `EmptyStateRows` — chosen-first then top-up excluding chosen; capacity bounds only the top-up;
  drops blank display names never happen (DisplayName is non-blank) but **non-positive ids are
  dropped**; de-dupe by id.
- `SearchResultRows` — matches mapped unselected, excluding already-chosen; de-dupe.
- `Toggle` — unknown→`Added`, chosen→`Removed`, never `LockedNoOp`; inputs not mutated.
- `ShouldRunSearch` — equal stamp runs, stale skips.
- `ToTarget` — a chosen member/row yields `{userId = Id, displayName = DisplayName}`; a **spaced**
  display name round-trips by id (the raw name string is never parsed for the id); the `DisplayName`
  fallbacks (username → email local part → `User {id}`) surface correctly.

TUI glue (`MentionPickerView`) is CI-untestable Terminal.Gui and is covered by `dotnet build` here;
interactive `tui-validate` lands with the consumer wiring (#325/#326).
