# Multi-tab (1): harden `state.db` for concurrent processes (#293)

Part of the Multi-tab epic (#292). Foundation for every other sub-issue. Make the shared local store
(`state.db`, LiteDB in `ConnectionType.Shared`) safe when two+ instances write it concurrently, so
multi-tab use can't crash a tab or silently lose a user's settings/pins/learned frequency entries.

## Problem (verified in repo)

LiteDB's shared-mode global mutex already prevents file corruption / torn reads across processes. The
gap is **logical last-writer-wins clobbering** and **unswallowed failures**, in three places:

1. **Whole-document read-modify-write from a startup snapshot:**
   - `ConfigStore.Save` rewrites the entire `AppConfig` (pins, F3 view, settings, per-task working
     dirs). A concurrent tab's field change is discarded on the next save.
   - `AssigneeFrequencyCache.Persist` / `ListFrequencyCache.Persist` write the whole in-memory set;
     neither re-reads disk first, so entries the *other* process learned after startup are lost.
2. **Unguarded loads:** `AssigneeFrequencyCache.Load` / `ListFrequencyCache.Load` call
   `_store.Load<TDocument>` with no `try/catch`, so a malformed payload throws out of the constructor
   (unlike `TaskCache`/`FeedCache`, which treat a corrupt payload as a clean miss).
3. **Unswallowed writes:** `ConfigStore.Save`, `TaskCache.Save`, `FeedCache.Save` let a
   `LiteException`/IO error propagate — a contended write could crash the UI action that triggered it.
   (The two frequency `Persist` methods already swallow.)

`tasks`/`feed` snapshot caches are single-document "last snapshot wins" **by design** (non-authoritative,
re-fetched from the API on next launch) — safe to leave racing; just record why in a comment.

No structured logging exists in this codebase; the established convention is swallow-with-a-comment
(see the frequency `Persist` methods). We follow that — "logged" means best-effort swallow + comment,
not a new logging subsystem.

## Design

### Phase 1 — swallow writes + guard the unguarded loads
- `AssigneeFrequencyCache.Load` / `ListFrequencyCache.Load`: wrap the `_store.Load` in
  `try/catch (JsonException)` → treat a malformed payload as an empty pool (mirrors `TaskCache`).
- `ConfigStore.Save`, `TaskCache.Save`, `FeedCache.Save`: wrap the store write in `try/catch` so a
  failed write is swallowed, never fatal. Add the "last-writer-wins is safe here" comment to the two
  snapshot caches (non-authoritative, re-fetched on launch).

### Phase 2 — frequency caches: merge-before-persist (union)
- Add pure `AssigneeFrequency.Merge(acc, incoming)` and `ListFrequency.Merge(acc, incoming)`:
  for each incoming entry, **union its distinct-task-id set** into `acc` (count = distinct-task count,
  so union is the correct "union counts") and keep the existing non-blank name (else the incoming
  name). Returns whether `acc` changed. Mirrors the existing `Accumulate`/`Seed` shape.
- In each cache's `Persist` (already under `_gate`, already swallowing the write): first re-read the
  disk document (guarded); if it matches this workspace + schema, `Merge` its entries into `_entries`
  before serialising. A concurrent tab's additions are unioned in rather than clobbered. Persist still
  only fires on a local change, so the extra read rides the existing (rare) write path.

### Phase 3 — config: three-way merge-before-save (per-field last-writer-wins)
- New pure helper `Configuration/ConfigMerge.cs`: `ThreeWay(baselineJson, currentJson, onDiskJson)`
  operating on the top-level JSON object. For each top-level property: if `current` differs from
  `baseline` (this process changed it) → take `current`; else → take `onDisk` (preserve the other
  process's change). Each top-level property (incl. nested `view`, `agentDispatch`, `pinnedTaskIds`)
  is one "field", last-writer-wins as a unit — exactly the spec's "last-writer-wins per field on a
  fresh read".
- `ConfigStore` keeps a `_baseline` (deep clone of the config, captured on `Load`; refreshed to a clone
  of the just-saved `current` after each `Save`). `Save(current)`:
  1. Load the on-disk config (guarded). If none, or no baseline yet → write `current` directly.
  2. Otherwise serialise baseline/current/onDisk with `StateJson.Options`, `ConfigMerge.ThreeWay`
     them, deserialise the merged JSON, and write that.
  3. Swallow a failed write; set `_baseline = clone(current)` (mirrors this process's known state so
     only genuine future local edits are treated as changes — untouched fields stay deferred to disk).
- No `TodoApp` call-site changes — the fix is entirely inside `ConfigStore`. Signature stays `void`.

## Tests (xUnit, all offline)
- **Phase 1:** corrupt-payload load → empty pool, no throw (both frequency caches); throwing store →
  `ConfigStore.Save`/`TaskCache.Save`/`FeedCache.Save` don't throw.
- **Phase 2:** pure `Merge` (union task-ids, name preference, new entries); two caches over one store,
  interleaved record → neither loses the other's entries (the concrete clobber the issue calls out).
- **Phase 3:** pure `ConfigMerge.ThreeWay` (current-wins, disk-wins-when-unchanged, both-changed →
  current wins, new key); two `ConfigStore`s over one file store, interleaved edits → neither loses the
  other's field (Refresh + BadgeDisplay + a pin).

## Out of scope / deferred
- No in-process write queue (LiteDB's mutex already serialises — the issue says don't add one).
- The nudge-then-fetch cross-process *signalling* is #294/#295, not this issue.
- TTL/eviction of the frequency pools is #124.
