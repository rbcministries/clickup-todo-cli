# Multi-tab nudge channel — writer (#294)

Part of the multi-tab epic (#292). The **producer** half of the nudge-then-fetch
design: after a write is confirmed by ClickUp (HTTP 2xx), record a tiny marker in
`state.db` so other running instances know *which task* changed — without putting the
changed **data** in the store. The **consumer** that scans these markers and refetches
is #295 (separate PR).

Depends on #293 (`state.db` hardening — merged in #332), whose swallow-on-write
discipline this mirrors: a nudge that fails to persist must never break the
already-succeeded user edit.

## Data model

A `changes` LiteDB collection, **keyed by task** (one row per task, upsert supersedes) —
not an append-only log. Nudge-then-fetch always re-fetches the whole task, so a task's
edit *history* is irrelevant to consumers (three rapid edits and one edit both resolve to
a single fetch of the final state). Keying by task bounds the table by *distinct tasks
touched*, not by number of writes.

```
changes collection (state.db):
  _id                 = taskId          // one row per task (upsert supersedes)
  Seq                 = <long>          // monotonic cursor consumers scan against (#295)
  ServerDateUpdatedMs = <long?>         // ClickUp's confirmed date_updated (null when the
                                        //   write response doesn't carry it — e.g. comment post)
  ChangedFields       = ["status"]      // advisory (the fetch gets everything)
  InstanceId          = <string>        // lets a reader skip its own writes (#295)
  RecordedUtcMs       = <long>          // local stamp, for TTL aging only
```

Two clocks, two jobs:

- **`ServerDateUpdatedMs`** — ClickUp's confirmed `date_updated` from the write response.
  Lets a consumer that already holds the task suppress a redundant fetch when its copy is
  already ≥ this version. Null when the write endpoint doesn't return it (comment post),
  which just means "consumer can't suppress — always fetch": safe.
- **`Seq`** — a local monotonic counter (its own `changes_seq` doc), the cursor #295 scans
  against.
- **`RecordedUtcMs`** — a local wall-clock stamp used only for TTL aging (ClickUp's clock
  would skew across replicas, and comment markers carry no server time).

## Emission point — the facade (`ClickUpClient`)

Markers are emitted from `ClickUpClient`, the single write choke point, because that is the
one place that simultaneously has (a) the 2xx signal, (b) the server-confirmed
`date_updated` in hand, and (c) uniform coverage of every write type and every host
(dashboard, single-task mode #296, …) for free. Emission after the `await …PutAsync`
inside `Guard` runs **only on 2xx** — a non-2xx throws before reaching it — giving the
"failed write emits nothing" AC for free. `ClickUpClient` takes an optional
`IChangeMarkerStore` (default null ⇒ no emission), so existing constructors, the offline
facade write tests, and the E2E/integration paths are unaffected unless they opt in.

Emit from: status, priority, description save, assignee add/remove, comment post.
`ServerDateUpdatedMs` is `date_updated` parsed off the `PUT /task` response for the first
four; **null** for comment post (its response returns only the comment id/date, not the
task's `date_updated`). Membership writes (`AddTaskToListAsync`/`RemoveTaskFromListAsync`)
are out of scope per the issue's write list.

## Atomic `seq` allocation

The one spot that needs real care: two tabs writing at the same instant must not collide on
a `seq`. `LiteDbChangeMarkerStore.Record` runs allocate-seq + upsert-marker + trim under:

1. an **in-process lock** (serialises this process's own threads), and
2. a **LiteDB transaction** — in `ConnectionType.Shared` (how `state.db` is opened) a
   transaction holds the cross-process named mutex for its whole duration, so the
   read-counter → write-counter → upsert sequence is atomic across processes too.

Verified by a concurrency test driving two store instances (two connections over one file =
the cross-process path) from many threads and asserting every `seq` is unique and the set is
exactly `1..N`.

## Aging the marker table

Keyed-by-task the table is largely self-bounding; two caps back it up (never ack-based —
readers come and go, so we can't know "every reader saw it"), both piggybacked on the write
path (cheap — already in the transaction):

- **TTL** — drop markers older than a small multiple of the full-resync interval (the list
  resyncs fully every ~10 poll cycles). Once a resync cycle elapses every live tab has
  already converged via normal polling, so the marker is dead weight. Default 30 min,
  configurable.
- **Count cap** — keep the newest N by `Seq` (default 500) as a backstop against pathological
  churn; trim older on write.

## Components

- `Configuration/ChangeMarker.cs` — the marker record + `ChangeMarkerOptions` (TTL, count
  cap, `TimeProvider`).
- `Configuration/IChangeMarkerStore.cs` — `InstanceId`, `Record(taskId, serverDateUpdatedMs,
  changedFields)`, `ReadAll()` (ordered by `Seq`; the base #295 builds its cursor scan on).
- `Configuration/NullChangeMarkerStore.cs` — no-op (file backend / emission disabled).
- `Configuration/LiteDbChangeMarkerStore.cs` — the real store over the shared `LiteDatabase`.
- `LiteDbStateStore.CreateChangeMarkerStore(instanceId, options)` — builds the store over
  the same `state.db` connection (keeps the `LiteDatabase` encapsulated).
- `Program.cs` — generate a per-process `instanceId`, build the marker store, pass it into
  `ClickUpClientFactory.Create` → `ClickUpClient`.

## Phases

1. Marker model + store (`IChangeMarkerStore`, `NullChangeMarkerStore`,
   `LiteDbChangeMarkerStore`) + store-level unit tests (upsert/supersede, seq
   monotonicity + cross-connection atomicity, TTL + count-cap trim, swallow). *Draft PR opens.*
2. Facade emission — inject `IChangeMarkerStore` into `ClickUpClient`; emit after 2xx from
   the five write types with the confirmed `date_updated`; offline facade tests via the
   capturing `HttpMessageHandler` (emits on 2xx per write type; no emit on a non-2xx/thrown
   write).
3. Wire the composition root (`LiteDbStateStore.CreateChangeMarkerStore`, `Program.cs`
   instanceId + factory pass-through). Build/test/format green; mark ready.

## Out of scope (this PR)

- The **consumer** (marker scan + nudge-then-fetch refresh) — #295.
- Membership-write markers — not in the issue's write list.
- Element-level set-union merge for `pinnedTaskIds` — #335.
