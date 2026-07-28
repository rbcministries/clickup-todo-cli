# Fix flaky `ConcurrentRecords_AcrossTwoConnections_NeverCollideOnSeq` (#410)

`LiteDbChangeMarkerStoreTests.ConcurrentRecords_AcrossTwoConnections_NeverCollideOnSeq`
fails intermittently on a full `dotnet test` run (observed during #318's work),
but passes 3/3 in isolation and on re-run. This plan pins down whether the flake
is in the test's assumptions or in `LiteDbChangeMarkerStore`, and fixes the real
cause without weakening the test.

## What the test asserts

Two `LiteDbStateStore` connections (A, B) over one `state.db` file, each hammering
`4 threads × 25` distinct-task `Record` calls (200 markers total), then a third
connection reads back and asserts:

1. `seqs.Count == 200` — no marker was lost.
2. `seqs.Distinct().Count() == 200` — no two markers share a `Seq` (**collision**).
3. `seqs.OrderBy == 1..200` — the seqs are **contiguous and monotonic from 1**.

## What the store actually guarantees (the contract)

`ChangeMarkerConsumer` (#295) — the only consumer — treats `Seq` purely as a
**monotonic cursor**: `Advance` sorts by `Seq`, advances its cursor past every
marker it sees, and re-picks-up a task when a re-edit upserts a *higher* `Seq`.
It needs `Seq` to be **unique and monotonic**; it tolerates gaps. Contiguity
(assertion 3, `1..N` with no gaps) is therefore *stronger* than the consumer
requires — though the store's own XML docs do describe the allocator as producing
a climbing `id+1`, so it is an intended property in the no-contention case.

`Seq` is allocated by `AllocateSeq()`: a LiteDB auto-increment `Insert` into a
`changes_seq` collection, then `DeleteMany(Id < new)` to keep only the watermark
row (so auto-increment keeps climbing rather than resetting from an empty
collection). Every write is wrapped in a blanket `catch {}` so a nudge can never
break the already-succeeded edit it rides on.

## Investigation

- **Isolated reproduction, no external load: 0 failures / 300 rounds.** A
  standalone harness replaying the exact two-connection hammer, looped 300×,
  never failed. This matches LiteDB's `SharedEngine`: two same-process
  `ConnectionType.Shared` connections to one file are serialized by a named OS
  mutex around each open→op→close, so their `Insert`/`DeleteMany`/`Upsert`
  operations cannot interleave — no collision, no gap, no loss when the machine
  is otherwise idle.
- The flake therefore needs the **full suite's** concurrent load (dozens of test
  collections running in parallel → thread-pool saturation + GC pauses), which
  is exactly the documented trigger ("fails only when the rest of the suite is
  running in parallel").

- **The failure is a lost marker, not a collision or a gap.** The test file has
  a single commit (#347) and is unchanged since, so the issue's stack frame
  `LiteDbChangeMarkerStoreTests.cs:132` maps to the *current* line 132 —
  `Assert.Equal(total, seqs.Count)`, the **count** assertion (line 133 is
  no-collision, line 134 is contiguity). The count assertion fired, so
  `seqs.Count < 200`: one (or more) `Record` call did not persist its marker.
- Reproduced by looping the **full** `dotnet test` suite: it fails ~1 run in ~23
  (~4%). It does **not** reproduce in a standalone loop of the same two-connection
  hammer (0 / hundreds of rounds), even with concurrent rounds — confirming the
  trigger is the suite's global pressure, not the two-connection contention alone.

## Root cause

`Record` wraps its whole write (`AllocateSeq` → `Upsert` → `Trim`) in a blanket
`catch {}` so a nudge can never break the edit it rides on. Under the full
suite's load, a LiteDB `ConnectionType.Shared` operation on the marker file
transiently throws (the file is opened/closed per operation in Shared mode, and
under GC/thread-pool pressure a reopen can briefly fail). The blanket catch
**silently drops that marker** — a lost cross-process nudge. The test catches it
as `Count < total`. This is a real robustness gap: the store is explicitly built
for two tabs writing at once, and that is exactly when it drops a nudge.

## Fix

Make the write **resilient to transient contention** instead of dropping on the
first failure: a small bounded retry (with backoff) around the write, falling
back to the existing swallow only after attempts are exhausted (so the "never
break the edit" contract is preserved). Structured to preserve seq integrity:

- `AllocateSeq` makes its watermark-trim `DeleteMany` best-effort (its own
  `try/catch`), so it either returns a freshly-allocated id or throws having
  allocated nothing — a retried allocation never burns/skips a seq.
- The retry memoises the allocated seq across attempts, so a transient failure in
  the marker `Upsert`/`Trim` re-runs with the *same* seq (idempotent upsert) —
  no gap, no collision, and eventually no loss.

Extract the retry into a tiny injectable policy (`maxAttempts` + a delay hook) so
its logic is unit-tested deterministically, with the delay hook made instant in
tests.

## Tests

- New unit tests for the retry policy: a work action that throws _k_ times then
  succeeds runs exactly _k+1_ times and does **not** hit the give-up path; an
  always-throwing action gives up exactly once after `maxAttempts` (swallowed).
- Keep the existing concurrency integration test's correctness assertions
  (count/no-collision/monotonic) intact — never weakened.

## Tests

- Keep the existing concurrency test's correctness assertions (uniqueness,
  no-loss) intact — never weakened to go green.
- Add targeted coverage for whichever failure mode the fix addresses, at the
  unit level where possible so it runs deterministically in CI.
