# Element-level set-union merge for `pinnedTaskIds` (#335)

Follow-up to #293 (multi-tab `state.db` hardening), part of the #292 multi-tab epic.

## Problem

`ConfigMerge.ThreeWay` (`src/ClickUpTodo/Configuration/ConfigMerge.cs`, #293) three-way
merges the settings document **per top-level field, last-writer-wins as a whole unit**. That
protects *different-field* concurrent edits (tab A changes the refresh interval, tab B pins a
task → both survive). But the `pinnedTaskIds` **array** is one such whole-field unit, so two
tabs editing it inside one load→save window resolve LWW for the entire array:

- Tab A pins task X (`pinnedTaskIds = [X]`), tab B pins task Y (`[Y]`), neither has saved yet.
- Whichever saves second writes its own array wholesale; the other tab's pin is dropped.

## Dependencies (all landed on `main`)

- `ConfigMerge.ThreeWay(baseline, current, onDisk)` — #293, `f2b4891`. Pure, JSON-string in/out,
  unit-tested in `ConfigMergeTests`.
- `AssigneeFrequency.Merge` / `ListFrequency.Merge` — #293. Precedent for element-level union of a
  persisted collection (union the sets, don't LWW the whole field).

No spec edit / Kiota regen, no new ClickUp API surface, no UI keybinding, no TUI change.

## Design

Give `pinnedTaskIds` a **three-way set merge** against the baseline, instead of whole-array LWW.
This must be three-way (not a blind `current ∪ onDisk`) so a genuine unpin is honored rather than
resurrected by the union.

### The set-merge rule

For each id in `baseline ∪ current ∪ onDisk`, with membership flags `b`/`c`/`d`:

- **added** by a side ⇒ present now but not in baseline: `(!b && c) || (!b && d)`.
- **removed** by a side ⇒ in baseline but gone now: `(b && !c) || (b && !d)`.
- **kept from baseline** ⇒ `b && !removed` (survived on both sides).
- **included** ⇒ `added || keptFromBase`.

Acceptance-criteria checks:

- Two tabs pin different tasks — `baseline=[]`, `current=[X]`, `onDisk=[Y]`: X added, Y added →
  `[X, Y]`. Both pins survive. ✓
- A tab unpins while the other does nothing — `baseline=[Z]`, `current=[]`, `onDisk=[Z]`: Z is
  `b && !c` ⇒ removed → excluded → `[]`. The unpin sticks, not resurrected. ✓
- Symmetric for a disk-side unpin (`baseline=[Z]`, `current=[Z]`, `onDisk=[]`) → `[]`.

### Ordering & shape

The result preserves **`current`'s order** for its included ids, then appends `onDisk`-only
additions in disk order — so when only this process changed the field, the output matches the
prior current-wins ordering, and de-dup is by ordinal string equality. Non-string / malformed
array elements are ignored defensively (the field is `List<string>`), and a missing/`null`/
non-array value on any side is treated as the empty set.

### Where it lives

A field-specific hook inside `ConfigMerge`: a small allow-list of top-level array fields that get
element-level string-set union (`pinnedTaskIds` today), consulted in the per-key loop before the
default whole-field LWW choice. Everything else keeps its current per-field LWW behavior. This
matches the issue's first suggested shape ("a field-specific hook for array fields that should
union") and keeps `ConfigStore`'s stateful glue untouched — it already supplies the three
serialised snapshots.

## Tests (`ConfigMergeTests`)

Pure-merge unit tests mirroring the existing style (JSON-string in, `JsonObject` out):

1. Two tabs pin different tasks → union `[X, Y]`.
2. This-side unpin, other side idle → stays unpinned (not resurrected).
3. Disk-side unpin, this side idle → stays unpinned.
4. Concurrent add on one side + unpin of a *different* id on the other → both honored.
5. Idempotent: baseline == current == onDisk → unchanged (re-merging our own write is a no-op).
6. Order: current's order preserved, disk-only additions appended.
7. Other fields keep per-field LWW (regression guard alongside the existing whole-field tests).

`ConfigStoreTests` already covers the stateful baseline/re-read/persist path; a focused case there
asserts the end-to-end concurrent-pin scenario through `ConfigStore.Save` if the pure coverage
leaves a gap.

## Out of scope

- No change to whole-field LWW for any non-array field or for other array fields
  (`taskWorkingDirectories` is a map, not a set; `excludedStatuses` is legacy migration-only).
- No new config field, no migration, no UI.
