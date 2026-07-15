# ADR: background refresh — per-group cadence gating on the existing loop (#246)

**Status:** proposed · **Decision issue:** #246 · **Prompted by:** #208/#236 (list-hierarchy walk)

## Decision in one paragraph

Adopt **axis 1 in its minimal form** (option A: per-group "due?" gating inside the existing
single `RefreshService` loop, driven by a small `TimeProvider`-injected cadence gate) and
**recognize that axis 2 is already adopted where it earns its keep** (screen-local
`Application.AddTimeout` loops for the feed and the task detail screen). Do **not** build a
scheduler/worker pool (B) or add more free-running background loops for dashboard concerns (C).
The first consumer is #236's list-hierarchy walk (~30 min cadence); nothing else changes tempo.

## Corrections to the issue's "verified current state"

Two premises in #246 are stale, and they materially change the risk calculus:

1. **#193 is closed and landed.** `ClickUp/ClickUpRateLimitHandler.cs` sits innermost on the
   shared `HttpClient` (`ClickUpClientFactory.cs:65`) and already provides the process-wide
   in-flight budget (`MaxInFlight = 6`), bounded 429 retry honouring `Retry-After` /
   `X-RateLimit-Reset`, a proactive low-budget pause, and jitter. "Decoupling makes the missing
   budget more pressing" no longer holds — the backstop exists. It bounds *concurrency*, though,
   not *schedule alignment* (see burstiness, below).
2. **#123's separate feed cadence is fully realized.** `AppConfig.FeedRefreshSeconds` (default
   300 s) exists, is editable in Settings, and is what the feed screen's timer is seeded from
   (`TodoApp.cs:561`, `NotificationsFeedScreen.cs:88-120`) — not `RefreshSeconds`.
3. **The inventory missed a third mechanism:** `TaskDetailScreen` auto-refreshes every 30 s via
   its own screen-local `Application.AddTimeout` (`TaskDetailScreen.cs:429`), armed on show and
   torn down on dispose, exactly like the feed's.

So the codebase already has a de-facto two-tier architecture: **one poll loop for the dashboard
snapshot + its coupled resolvers**, and **screen-scoped timers for screen-scoped data, armed only
while visible**. That second tier *is* axis 2, scoped to where isolation is actually needed, and
it has been working. #246's real gap is only: the single loop has no notion of "this piece is due
every 30 min, not every cycle" — which is precisely what #236 needs.

## Fetch inventory (natural cadence × coupling group)

| Fetch | Where | Coupling group | Natural cadence | Today |
|---|---|---|---|---|
| Task snapshot (delta/full) | `TodoApp.FetchAsync` → `TaskService.LoadSnapshotAsync` | **snapshot** (anchor) | `RefreshSeconds` (60 s) | every cycle |
| Context parents | `ResolveContextParentsAsync` | **snapshot-derived** | with each new snapshot | every non-empty cycle |
| Foreign subtasks | `ResolveForeignSubtasksAsync` | **snapshot-derived** | with each new snapshot | every non-empty cycle |
| List colors | `ResolveListColorsAsync` | **snapshot-derived** (cached per process) | with each new snapshot | every non-empty cycle |
| Full resync | `FullResyncEveryNthPoll = 10` | snapshot | every 10th poll | already cadence-gated (by cycle count) |
| List-hierarchy walk (#236) | proposed | **independent** of the snapshot | ~30 min, spread across cycles | doesn't exist yet |
| Notifications feed | `NotificationsFeedScreen` timer | independent, screen-scoped | `FeedRefreshSeconds` (300 s) | own timer, only while shown |
| Task detail + comments | `TaskDetailScreen` timer | independent, screen-scoped | 30 s | own timer, only while shown |
| Statuses / members / assignees | `StatusCache` etc. | independent | rarely; on demand | TTL caches, refresh-on-access |

Two structural observations fall out:

- The only *coupled* group is snapshot + its three resolvers, and they are already parallelized
  (`Task.WhenAll`, pay-for-the-slowest, `TodoApp.cs:292`) and already skipped wholesale on a
  provably-empty delta. There is no serialized convoy left to break up *within* the dashboard
  cycle.
- Everything that wanted its own tempo either already has it (feed, detail, TTL caches) or is the
  one newcomer (#236's walk). The "problem" is one missing scheduling primitive, not a missing
  architecture.

## Options weighed

### A — per-group due-gating on the single loop (recommended)

Track `lastRanAt` per group; on each cycle, run only groups that are due. No new threads.

- **Pros:** smallest diff; coupling constraint holds by construction (snapshot-derived resolvers
  stay inside the same `FetchAsync` pass over the same snapshot); delta short-circuit and
  10th-poll resync untouched; trivially unit-testable with a fake `TimeProvider`; covers #236's
  need completely; error handling, cancellation, and manual-wake stay in one place.
- **Cons:** cadence granularity is quantized to the base interval — a group runs at the first
  cycle *after* it becomes due (with `RefreshSeconds` clamped to 10–3600 s, worst-case a
  "30-min" group runs every 90 min if the user sets 3600 s; acceptable — express group cadences
  as *minimum ages*, not exact periods, and document it). A slow due group still shares the
  cycle with the snapshot — but it joins the existing `WhenAll`, so it adds max-cost, not
  sum-cost, and #236's walk is explicitly spread across cycles anyway.

### B — real scheduler + worker pool

- **Pros:** exact cadences, priorities, one queueing point; scales to dozens of fetch kinds.
- **Cons:** the most new machinery (queue, pool, per-item error/backoff policy, priority or
  starvation rules, a second concurrency layer interacting with the HTTP gate); the coupling
  constraint has to be *enforced* (group = schedulable unit, stale-enrichment races become
  possible) instead of holding by construction; hardest to test and to reason about under
  `tui-validate`'s latency/volume guards. We have ~5 fetch concerns, three of which must stay
  together. This is machinery for a problem the app doesn't have.

### C — more decoupled free-running loops

- **Pros:** isolation (a slow fan-out can't delay the cheap row refresh); each concern owns its
  tempo; precedent exists.
- **Cons:** the isolation win is already banked where it matters — the two heavy, screen-scoped
  concerns (feed, detail) *are* decoupled, and visibility-scoped arming means they cost zero API
  calls when not shown. For dashboard concerns, N loops mean N wake triggers, N error paths, more
  background writers into UI-thread-owned state (`_contextParents`, `_foreignSubtasks`, `_rows`),
  and unsynchronized snapshot/enrichment versions — the exact coherence hazard the issue flags.
  The HTTP gate caps concurrency but not *burst alignment*: independent loops all fire at t=0
  after launch and re-align after any stall, which is the thundering-herd shape the
  `SetupWizard.cs:192` caution is about.

### B+C

Everything above, combined. Not justified at this scale.

## Strategies the issue didn't consider (and what we take from them)

1. **TTL / refresh-on-access instead of time-driven push.** Several "fetches" are not naturally
   periodic — they are *caches with staleness tolerances* (statuses, members, list colors, and
   arguably the list hierarchy). The repo already models this (`StatusCache`, task/feed cache
   max-age from #124). A cadence scheduler that "runs statuses every N min" spends API budget
   whether or not anyone looks; TTL-on-access spends it only when needed. **Take:** keep TTL
   caches as-is; model #236's walk as a *cache backfill with a minimum age*, not a hard timer —
   which is exactly what due-gating expresses.
2. **The delta short-circuit starves independent groups.** `FetchAsync` returns early on a
   provably-empty delta, skipping *all* resolvers. Correct for snapshot-derived work; wrong for
   an independent group like the hierarchy walk — on a quiet workspace it would never run.
   Due-gating must be evaluated per *coupling class*: snapshot-derived groups sit behind the
   delta check; independent groups are gated only by their own age. (This is the one real design
   subtlety; nothing in A/B/C as written in the issue addresses it.)
3. **Run-collapsing / no queueing of missed runs.** If a group is due but its previous run is
   somehow still in flight, or two cycles pass while it runs, it must run once, not accumulate a
   backlog. Timer-queue designs get this wrong by default; "due = age ≥ interval, mark on
   completion" gets it right by default. **Take:** stamp `lastRanAt` at *completion*, not start.
4. **Phase offset / jitter for scheduled work.** The rate-limit handler jitters *retries*, but
   scheduled fetches still align (everything fires on the same cycle tick). With due-gating this
   mostly self-solves — groups piggyback on an existing cycle rather than owning a timer — but
   #236's walk should still spread its space-by-space enumeration across *successive* cycles
   (bounded branches per cycle, resume next cycle) rather than walking everything on the cycle
   it becomes due.
5. **Idle backoff (adaptive cadence).** N consecutive empty deltas → stretch the effective poll
   interval (bounded, e.g. ×2 up to 5 min); any user activity or manual refresh resets it. This
   is the single biggest API-spend reducer available and neither axis in the issue captures it.
   **Take:** note as an optional follow-up, gated on measurement — not part of this change.
6. **Rate-budget-aware deferral.** The handler already parses `X-RateLimit-Remaining`; a due
   *low-priority* group (the walk) could be deferred when the remaining budget is low. Cheap to
   add later because the handler is the single choke point. Follow-up only.
7. **Per-group error backoff.** A persistently failing group should back off independently
   instead of failing the shared cycle at full cadence. With due-gating, a failed run can simply
   count as "ran" (its next attempt waits a full interval) — good enough; no extra machinery.

## Decision on the two axes

- **Axis 1 (per-fetch cadence): adopt, minimally.** Per-group minimum-age gating evaluated
  inside the existing loop. No queue, no worker pool.
- **Axis 2 (decoupled execution): already adopted where warranted; do not extend.** Screen-scoped
  fetches keep their visibility-scoped `Application.AddTimeout` loops. Dashboard concerns stay on
  the single `RefreshService` loop.

## Implementation plan (follow-up issues, no behavior change under #246)

1. **`FetchCadenceGate` primitive** (`Services/`): `bool IsDue(string group, TimeSpan minAge)` +
   `MarkRan(string group)`, `TimeProvider`-injected, UI-thread-free, unit-tested (due-at-boundary,
   run-collapsing, manual-refresh reset). ~50 lines.
2. **Wire into `FetchAsync`** with the two coupling classes made explicit:
   snapshot-derived resolvers stay exactly where they are (behind the delta check, inside the
   `WhenAll`); independent groups are evaluated *before* the empty-delta early return so a quiet
   workspace can't starve them. `RefreshKind.Manual`/`Initial` forces every group due (matching
   today's "manual is always full" semantics).
3. **First consumer — #236's hierarchy walk** as a fourth, independently-gated concern:
   `minAge ≈ 30 min`, bounded branches per cycle with resume (spread), results seeding the
   list-frequency cache. This is where #236's acceptance criteria get proven.
4. **Measure, don't assume** (the issue's own bar): under the `tui-validate` PTY harness with the
   fake backend, (a) assert keypress latency and output-volume guards are unchanged; (b) count
   backend requests over a scripted multi-cycle run — quiet polls must add **zero** calls from the
   new group until it is due, and the due cycle's added calls must match the per-cycle walk bound.
   The fake backend makes request-counting a plain assertion.
5. **Optional, measurement-gated follow-ups:** idle backoff (#5 above), rate-budget-aware
   deferral of low-priority groups (#6).

## Guardrails restated

- Delta / 10th-poll full-resync semantics unchanged (the gate composes with them; it does not
  replace them).
- No new free-running loops; concurrency stays bounded by the landed #193 gate + per-fan-out
  `MaxFanOutConcurrency = 4`.
- Single-toplevel and latency invariants (#3/#38) enforced by running `tui-validate` on the
  first consumer, not asserted by design review alone.
