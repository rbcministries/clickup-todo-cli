# Feed aggregation service (#112)

Part of the Mentions & Comments feed epic (#109). Turns per-task comments into a single
newest-first feed list. **No mention detection (#113) and no rendering (#114)** — this issue
delivers the domain service + its unit tests only.

## Acceptance criteria (from the issue)

- Returns a single newest-first feed list assembled from multiple tasks' comments.
- Fans out `GetTaskCommentsAsync`-style calls across the user's assigned tasks (reuse
  `GetAssignedTasksAsync`), merges results, de-dups by comment id, orders by `date` descending,
  caps the result count.
- Runs off the UI thread with sensible concurrency (so many tasks don't stall the feed).
- Covered by unit tests (ordering, de-dup, cap); `dotnet test` green.

## Design

A new `FeedService` in `src/ClickUpTodo/Services/FeedService.cs`, split into a thin glue method
and two pure/near-pure `internal static` cores (mirroring the `TaskService` /
`ForeignDescendants` split so the CI-testable logic lives in static methods reachable via
`InternalsVisibleTo`):

- **`Aggregate(perTask, maxEntries)`** — *pure*. Flattens the per-task comment lists, de-dups by
  non-empty comment id (first occurrence wins; empty ids pass through as distinct), orders newest
  first (`DateMs` descending, nulls last, `Id` ordinal tiebreak for determinism), and caps to
  `maxEntries`. This is the heart of the acceptance criteria and is exhaustively unit-tested.
- **`GatherAsync(taskIds, fetchComments, maxConcurrency, maxEntries, ct)`** — *near-pure glue*.
  Fans out `fetchComments` over the task ids with a `SemaphoreSlim`-bounded concurrency, best-effort
  per task (a task whose comments can't be fetched contributes nothing — mirrors
  `ResolveContextParentsAsync`/`ResolveForeignSubtasksAsync`; genuine `OperationCanceledException`
  propagates), then feeds the results through `Aggregate`. `fetchComments` is a delegate, so the
  fan-out (concurrency bound, best-effort skip, aggregation) is unit-testable with an in-memory
  fake — no live client needed.
- **`LoadFeedAsync(ct)`** — *glue*. Resolves the view's assignee ids via
  `TaskService.ResolveAssigneeIdsAsync`, calls `client.GetAssignedTasksAsync(workspaceId, ids, ct)`
  for the actionable task set, then `GatherAsync(taskIds, client.GetTaskCommentsAsync, …)`. Naturally
  runs off the UI thread when awaited; the eventual screen consumer (#114) invokes it via the
  `Task.Run` + `Application.Invoke` pattern used by `OpenDetail()`.

Constants: `DefaultMaxConcurrency = 8`, `DefaultMaxEntries = 200` (documented caps — the cap keeps
the newest entries and is called out in the XML docs so it never reads as "complete history").

Result type is `IReadOnlyList<CommentItem>` — the existing stable record already carries `TaskId`
attribution (#111). No new model. `#113` will add mention flags; `#114` renders.

### Why a separate service (not a `TaskService` method)

The feed has its own fan-out/concurrency/cap policy and will grow (mention filter #113, background
refresh #116, cache #123). Keeping it out of `TaskService` avoids bloating the load path and keeps
each service's responsibility single. It depends on `TaskService` only for assignee resolution
(reusing the members cache), `ClickUpClient` for the two fetches, and `AppConfig` for workspace/view.

### Not wired into the UI

`#112` is explicitly non-rendering. Wiring `FeedService` into `TodoApp`/the feed screen is `#114`
(blocked by #110/#112/#113). Constructing an unused instance now would be dead code, so the service
ships with tests only and no `Program.cs`/`TodoApp` change.

## Tests (`tests/ClickUpTodo.Tests/FeedServiceTests.cs`)

Against `Aggregate` (pure):
- Merges multiple tasks' comments into one newest-first list.
- De-dups by comment id across tasks (first wins); distinct empty-id comments are preserved.
- Null/absent `DateMs` sorts last; ordering is deterministic on ties (Id ordinal).
- Caps to `maxEntries` keeping the newest; non-positive cap yields empty; empty input yields empty.

Against `GatherAsync` (fake fetcher):
- Aggregates fanned-out results newest-first, de-duped and capped.
- Best-effort: a task whose fetch throws is skipped; the rest still appear.
- Concurrency is bounded: with `maxConcurrency = k` and a gate, peak in-flight never exceeds `k`
  and reaches `k` (deterministic gate + peak counter).
- Empty task-id set short-circuits to an empty feed.

## Phases

1. Plan (this doc) + `FeedService` + `FeedServiceTests`; build/test/format; open draft PR.
   (Single focused slice — no spec/Kiota change, no UI change.)
