# Plan: Closed-task prefetch follow-ups — cross-restart persistence (+ tui-validate bridge paint) (#280)

Follow-ups deferred from #253 (`.claude/plans/closed-task-prefetch.md`), which shipped a warm,
bounded, **in-memory** closed-task cache (`Services/ClosedTaskCache.cs`) that rides the refresh loop
and bridge-paints the F12→All transition. Two parts remain:

## Part 1 — Cross-restart persistence of the warm closed set (primary slice)

Today `ClosedTaskCache` is in-memory only, so the very first F12→All *after a fresh launch* still
stalls on the on-demand `include_closed=true` fetch until the first background prefetch warms the
cache (~1 poll interval). Persisting the bounded closed set through the existing `IStateStore` — like
`TaskCache`/`AssigneeFrequencyCache` — makes even the post-restart transition instant, warming from
`StateKeys` on startup.

### Design (mirrors the existing persisted caches)

- **`StateKeys.Closed`** = `"closed"` — one document, `closed.json` in the file backend.
- **`ClosedTaskCacheDocument`** (mirrors `AssigneeFrequencyDocument`): `SchemaVersion`, `Key`
  (context fingerprint), `Tasks`. No separate whole-doc TTL field — the per-task **age window** on
  `UpdatedMs` (`Bound`, 30 days) *is* the staleness bound, re-applied at load against the new launch
  time, so a set from a workspace untouched for a month self-empties on load.
- **Context guard = the fetch-scope fingerprint**, not just workspace. The closed set is fetched by
  the same `FetchMergedAsync` scope as the working set (workspace + Personal Tasks list + `Assignee
  IS` rules), so I reuse `TaskCache.KeyFor(config)` via a `Func<string>` the cache calls live — a
  personal-list / assignee-scope switch is a clean miss, never a transient foreign-closed bridge
  paint. Schema-version + key guards; a mismatch or corrupt payload is a clean miss (empty), never a
  crash (load runs synchronously at startup).
- **`ClosedTaskCache` gains optional persistence** (`IStateStore? store`, `Func<string>? contextKey`,
  appended after the existing positional params so `new ClosedTaskCache(clock, maxCount:, maxAge:)`
  call sites and tests keep compiling). Persistence is active only when both are supplied.
  - Ctor calls `Load()` when active: read → guard schema+key → **re-`Bound` against `now`** → hold.
    A `JsonException`/absent doc is a miss.
  - `Update()` persists the bounded kept set (best-effort, under the gate, `try/catch` swallowed like
    `AssigneeFrequencyCache.Persist` — a full/read-only disk must never break the refresh loop).
- **`TaskService`** constructs `_closedCache = new(timeProvider, store: stateStore,
  contextKey: () => TaskCache.KeyFor(config))`. No composition-root change — `TaskService` already
  receives `stateStore` and `config`. `WarmClosedTasks`/`PrefetchClosedTasksAsync`/`SupplementWithClosed`
  are untouched; they now transparently benefit from a warm-on-launch set.
- **`CacheReset.CacheKeys`** gains `StateKeys.Closed` so `--reset`/`--logout` forgets it too.

### Tests (Part 1)

`ClosedTaskCachePersistenceTests` (new) — real temp-dir `JsonFileStateStore` (like `TaskCacheTests`),
so the `TaskItem` JSON round-trip is exercised end-to-end:
- Update persists; a fresh cache over the same store + key loads the bounded set (round-trip incl.
  fields).
- Key mismatch → clean miss (empty).
- Schema-version mismatch → clean miss.
- Corrupt/garbage document → clean miss, no throw.
- Age re-bound on load: a task that has since aged past the window is dropped when a
  later-clock cache loads it.
- No `store`/`contextKey` (pure in-memory) → nothing persisted (existing behaviour preserved).
- `CacheReset.ClearAll` deletes the closed key (extend `CacheResetTests` if present).

## Part 2 — tui-validate bridge-paint scenario

#253's TUI wiring was verified by build + `tui-validate` A/B parity, but the fake E2E backend seeds
**no closed tasks**, so the *instant bridge paint itself* isn't asserted end-to-end. Add a scenario
that seeds a closed-type set on the fake backend, warms the prefetch (or exposes a warm-now hook),
presses F12 to All, and asserts the closed rows appear on the pre-refresh frame.

Assess feasibility against the actual harness after Part 1 is green. If it needs harness-seeding
plumbing beyond a clean slice this session, scope it to a linked follow-up issue rather than rushing
it (per the issue's own "file a follow-up if not covered here").

## Invariants

- Generated client / curated spec untouched — no API surface change (`include_closed` already exists).
- No second focusable pane (#3); no new keybinding (F12 already owns the cycle); no bare-letter
  shortcut (#12).
- Never weaken a test; integration tests stay `SkippableFact` + env-gated.
