# Clear the `(home)` marker when the seeded home list is de-selected (#370)

Surfaced while reviewing #241 (New Task multi-list create, merged in #366). A pre-existing
behaviour of the shared `SelectorView` distinguished set, visible now that the New Task screen is
the live user of `ListSelectorView`'s `(home)` marker.

No spec edit / Kiota regen, no new ClickUp API surface, no UI keybinding, no TUI structural change
(no second focusable pane — #3). Domain-support logic + one View-state mutation only.

## Problem

On the New Task screen, when a user **de-selects the seeded `(home)` list** and picks a different
list:

- The create target resolves correctly — `ListSelectorModel.ResolvePrimary` reads
  `SelectorView.DistinguishedSelection`, which is `_selected.Where(id ∈ _distinguishedIds)`, so once
  the seed leaves `_selected` the marker set no longer contributes and `Primary` falls through to the
  first remaining selection. **Functionally correct** (already covered by
  `ResolvePrimary_NoMarkedHome_FallsThroughToFirstSelected`).
- But the de-selected seed **keeps its `" (home)"` label** as an *unselected* candidate row. Root
  cause: `SelectorView.ApplyRemove` (`SelectorView.cs:289`) calls `RemoveFromSelection`, which clears
  `_selected` / `_selectedIds` but never prunes `_distinguishedIds` (or `_lockedIds`). The
  immediate-apply `Reconcile` path (`SelectorView.cs:342-343`) *does* prune both against
  `_selectedIds`; the collect-mode removal path does not. So the id lingers in `_distinguishedIds`,
  and when the same list re-surfaces in the empty-state **top-up** pool
  (`SelectorModel.EmptyStateRows` marks a top-up row `Distinguished` when its id is in
  `distinguishedIds` — an intentional behaviour, pinned by
  `EmptyState_MarksDistinguishedTopUp_WhenPrimaryIsUnselectedCandidate`), it renders the stray
  `(home)` marker on a row that isn't selected and isn't the target.

`EmptyStateRows` is **correct given its inputs** — the defect is the stale `_distinguishedIds`, not
the renderer. So the fix keeps `EmptyStateRows` untouched (and its existing test intact) and makes
the removal path keep the marker sets honest.

## Design

Mirror what `Reconcile` already does, in the collect-mode removal path.

### Pure seam (`SelectorModel.PruneMarkersToSelection`)

Extract the marker-prune into a pure, unit-testable helper (the two `RemoveWhere` lines `Reconcile`
inlines):

```
PruneMarkersToSelection(selectedIds, params markerSets)  // remove each id ∉ selectedIds from every marker set
```

`Reconcile` is refactored to call it (behaviour-preserving DRY), and `ApplyRemove` calls it too — so
the "a de-selected distinguished/locked seed stops being marked" invariant lives in one place.

### Where `ApplyRemove` prunes — collect mode only

The prune runs in `ApplyRemove` **only in `CollectSelection` mode**. Immediate-apply must *not*
prune here:

- On a **successful** server write, `Reconcile` rebuilds the selection from the confirmed set and
  prunes then — so the marker is already handled.
- On a **failed** write, the revert (`AddToSelection(item); RenderCurrent()`) re-adds the removed
  item; the marker must still be in `_distinguishedIds` for the restored row to render as home.
  Pruning eagerly in `ApplyRemove` would drop the marker permanently on revert — a regression.

Collect mode has no server round-trip, no `Reconcile`, and no revert, so it must prune inline or the
stale id is permanent — exactly the #370 symptom.

`_lockedIds` is pruned alongside `_distinguishedIds` for symmetry (the issue's request). Lists carry
no locked entry, so this is a no-op for `ListSelectorView` today, but keeps the base honest for the
assignee locked-default case if a future collect-mode host ever removes around a lock.

## Tests

`SelectorView` / `ListSelectorView` are Terminal.Gui composites; the suite never calls
`Application.Init` and never instantiates a View (see `NotificationsFeedScreenTests` /
`AgentRunModelTests` headers), so the fix is pinned at the pure-model seam the View wires together —
the repo's standard split.

`SelectorModelTests`:

1. `PruneMarkersToSelection` removes ids no longer selected from every passed set, keeps
   still-selected ids, and mutates in place.
2. `PruneMarkersToSelection` with an empty selection clears the sets.
3. **#370 regression (composed):** prune a de-selected distinguished seed, then `EmptyStateRows`
   renders the re-surfaced top-up row **unmarked** — the exact View sequence (remove → prune →
   render). Contrast with the retained `EmptyState_MarksDistinguishedTopUp_...` test, which shows the
   *un*-pruned input still marks (proving the renderer is unchanged and the fix is the prune).

The `Primary` half of the issue's acceptance is already covered by
`ResolvePrimary_NoMarkedHome_FallsThroughToFirstSelected` in `ListSelectorModelTests`.

## Manual verification (TUI — can't run in CI)

On the New Task screen: de-select the seeded `(home)` list, then pick a different list. The chosen
list shows `✓` (no marker); the de-selected seed no longer shows `(home)` when it re-appears as an
unselected candidate. Re-adding the previously-seeded home via search does **not** resurrect its
marker (it's no longer distinguished — matches the documented "re-added by search is never re-marked"
contract on `DistinguishedSelection`).

## Out of scope

- No change to `EmptyStateRows` (its top-up distinguished-marking is intentional and pinned).
- No change to the immediate-apply reconcile/revert behaviour.
- The Quick Updates List pane (#242) that also embeds `SelectorView` is disabled (#339); this fix is
  behaviourally safe there regardless (immediate-apply is unchanged).
