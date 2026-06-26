# Plan: Status-picker responsiveness (#10)

## Problem

Pressing `Space` to open the status picker can feel laggy. The blocker #3
(general input latency) is now **closed/completed**, and the maintainer's
diagnosis on #10 narrows the remaining lag to the **network fetch that happens
before the modal is shown**: `OpenStatusPicker` calls
`TaskService.GetStatusesForListAsync`, which fetches a list's statuses over the
network on first open. Today's cache (`Dictionary<string, IReadOnlyList<StatusOption>>`)
is session-lived with **no expiry** and is only populated **on first open**, so
the very first picker-open for each list pays a round-trip.

## Goal (from the maintainer's plan comment on #10)

1. **Background prefetch** — after each task load, prefetch statuses for the
   lists currently on screen, so the picker opens instantly in the common case.
2. **Cache with a long TTL** — statuses almost never change; cache them per-list
   and refetch at most once every ~10 minutes (timestamped cache) instead of the
   current never-expiring cache.
3. **Open immediately** — if a list's statuses aren't cached yet, don't block;
   surface a loading indicator and open as soon as data arrives.

Net effect: `Space` → picker feels instant in the common case, and we stop
hitting the API for status lists that haven't changed.

## Design

### New: `StatusCache` (testable, client-agnostic) — `Services/StatusCache.cs`

A small, thread-safe cache decoupled from `ClickUpClient` so it can be unit
tested without the network or mocking the sealed client. It is parameterized by:

- a fetch delegate `Func<string, CancellationToken, Task<IReadOnlyList<StatusOption>>>`,
- a `TimeProvider` (defaults to `TimeProvider.System`; tests inject a fake), and
- a TTL (`TimeSpan`, default 10 minutes).

API:

- `bool TryGetFresh(string listId, out IReadOnlyList<StatusOption>)` — synchronous
  hit check used by the "open immediately if cached" path. Returns false for a
  missing **or stale** (older than TTL) entry.
- `Task<IReadOnlyList<StatusOption>> GetAsync(string listId, CancellationToken)` —
  returns a fresh cached value, or fetches and caches. **De-dupes in-flight
  fetches** by `listId` so a prefetch already in flight is awaited rather than
  duplicated when the user presses `Space`.
- `Task PrefetchAsync(IEnumerable<string> listIds, CancellationToken)` — fetches
  only the missing/stale lists, swallowing per-list errors (best-effort warm-up).

TTL semantics: an entry fetched at `t` is fresh while `now - t < ttl`. A failed
fetch caches nothing (so it is retried next time) and is removed from the
in-flight map.

### `TaskService`

- Replace the raw dictionary with a `StatusCache`, built with
  `fetch = client.GetListStatusesAsync` and the injected `TimeProvider`.
- Add an optional `TimeProvider? timeProvider = null` constructor parameter
  (defaults to `TimeProvider.System`) — keeps `Program.cs` construction unchanged.
- `GetStatusesForListAsync` delegates to `StatusCache.GetAsync` (same signature,
  now TTL-aware + deduped).
- Add `bool TryGetCachedStatuses(string listId, out ...)` → `StatusCache.TryGetFresh`.
- Add `Task PrefetchStatusesAsync(IEnumerable<string> listIds, CancellationToken)`
  → `StatusCache.PrefetchAsync`.

### `TodoApp` (TUI — verified by build + reasoning, not unit-tested)

- **Prefetch**: at the end of `OnTasksLoaded`, fire-and-forget
  `_tasks.PrefetchStatusesAsync(distinct visible ListIds)`. `OnTasksLoaded` runs
  on the UI thread (via `Application.Invoke`); the prefetch is async and
  non-blocking, so it won't compete with input.
- **Open immediately**: in `OpenStatusPicker`, if
  `_tasks.TryGetCachedStatuses(listId, …)` hits, show the picker synchronously
  (no `Task.Run`, no round-trip). On a miss, keep the existing off-thread fetch
  with the `"Loading statuses…"` status-line indicator, then open when data
  arrives — already non-blocking. After prefetch, the miss path is rare.

This keeps the single sectioned `ListView` model intact (no second focusable
pane — #3) and does not change any keybindings.

## Tests (`tests/ClickUpTodo.Tests/StatusCacheTests.cs`)

Unit tests against `StatusCache` with a fake `TimeProvider` and a counting fetch:

- Cold miss fetches once; immediate second `GetAsync` is served from cache (fetch
  count stays 1).
- `TryGetFresh` is false before any fetch, true after, and false again once the
  clock advances past the TTL.
- After TTL expiry, `GetAsync` refetches (count increments) and returns the new
  value.
- In-flight de-dupe: two concurrent `GetAsync` for the same list trigger a single
  fetch (use a fetch that blocks on a `TaskCompletionSource`).
- `PrefetchAsync` warms only missing/stale lists and a subsequent `TryGetFresh`
  hits without a further fetch; a throwing fetch for one list does not fail the
  whole prefetch and caches nothing for that list.

All unit tests; no network. Existing integration tests (`SkippableFact`) are
unaffected.

## Out of scope / deferred

- A spinner *inside* the modal for the cold-open case. The status-line
  `"Loading statuses…"` indicator already covers the (now-rare) miss, and the
  fetch is off the UI thread. A richer in-modal loading state would add
  modal-repopulation complexity for marginal benefit; not pursued here.
