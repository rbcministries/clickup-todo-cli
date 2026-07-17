# Feed activity: narrow the recent-activity fetch server-side (#244)

Follow-up deferred from #117. The feed's recent-activity source is a pure client-side
projection of the assigned tasks the feed already fetches; this issue plumbs an optional
`date_updated_gt` window into that shared fetch so a busy workspace pulls fewer tasks.

## Goal

Add an **opt-in, default-off** look-back window (`FeedActivityLookbackDays`, `0` = disabled =
today's behavior) that narrows the feed's single `GetAssignedTasksAsync` fetch to tasks updated
within the last N days.

## Key facts (verified in the code)

- `FeedService.LoadFeedAsync` (`Services/FeedService.cs:67`) makes **one** fetch —
  `client.GetAssignedTasksAsync(workspaceId, assigneeIds, includeClosed, ct)` — whose result feeds
  **both** the comment fan-out and the recent-activity projection (`BuildActivity`).
- `date_updated_gt` is already a query param on the generated team-tasks builder and is set by the
  delta path (`ClickUpClient.GetAssignedTasksDeltaAsync:194` uses `cfg.QueryParameters.DateUpdatedGt`).
  It is **not** currently plumbed into the non-delta `GetAssignedTasksAsync`.
- `date_updated_gt` and `include_closed` are orthogonal query params; the delta method already
  combines both, so the F12 interaction is proven.
- `FeedService` is constructed once in `Program.cs:106` as `new FeedService(client, taskService, config)`.
  It reads `config` live, so a saved config change is picked up on the next `LoadFeedAsync`.
- Time is obtained via `TimeProvider` across the codebase (e.g. `FeedCache` ctor takes a nullable
  `TimeProvider`, defaulting to `TimeProvider.System`).

## Design decisions / interaction analysis (the "confirm before committing" items)

- **Comment fan-out (same task set feeds both).** Narrowing the shared fetch narrows the comment
  feed too. This is acceptable because posting a comment bumps a task's `date_updated`, so any task
  with a *recent* comment stays in-window; the window drops only tasks with no activity at all in
  the last N days (whose old comments would rarely surface anyway — the feed is newest-first and
  capped). Semantics: "activity/comments from tasks touched in the last N days." Default-off ⇒
  current behavior is byte-for-byte unchanged.
- **F12 completed toggle.** `include_closed` stays independent of the window; both params ride the
  same GET (the delta path already does exactly this). No coupling.
- **Standalone opt-in vs. "when activity is on".** The issue phrases it "when the activity source
  is on", but `FeedShowActivity` (F6) is a *display* toggle explicitly documented to be a
  client-side re-render that does **not** affect the fetch/cache key (`AppConfig.cs:62-69`).
  Coupling the fetch payload to a display toggle would contradict the #117 design. Instead the
  window is its own config setting (default off) that narrows the shared fetch whenever set — a
  cleaner realization of the same intent. Noted in the PR.

## Phases

### Phase 1 — service/client/config core (no UI)
- `ClickUpClient.GetAssignedTasksAsync` + `IClickUpClient`: add optional `long? updatedAfterMs = null`
  before `ct` (mirrors the delta method's param order). When non-null, set
  `cfg.QueryParameters.DateUpdatedGt`. Fix the one positional caller (`TaskService.cs:234`) to name `ct`.
- `AppConfig.FeedActivityLookbackDays` (int, default 0) with XML doc on the semantics above.
- `FeedService`: add `internal static long? ComputeUpdatedAfterMs(int lookbackDays, DateTimeOffset now)`
  (0 or negative => null; N>0 => `now.AddDays(-N).ToUnixTimeMilliseconds()`); inject a nullable
  `TimeProvider` (default `TimeProvider.System`); thread the computed window into the fetch.
- Tests: `ClickUpClient` request-building (CapturingHandler asserts `date_updated_gt` present when
  set / absent when null, and that `include_closed` composes); `FeedService.ComputeUpdatedAfterMs`
  (disabled at 0/negative, correct epoch-ms at N days).

### Phase 2 — settings UI surface
- `SettingsForm.ParseLookbackDays(text, fallback)` — parse/clamp to `[0, MaxLookbackDays]` (0 = off).
- `SettingsScreen`: a "Feed activity look-back (days, 0 = all):" numeric field; extend `SettingsResult`.
- `TodoApp`: apply `result.FeedActivityLookbackDays` into `_config` and persist.
- Tests: `SettingsForm.ParseLookbackDays` (parse, clamp, 0, invalid => fallback).

## Out of scope / deferred
- `order_by=updated` server-side ordering — the client-side newest-first sort over the bounded feed
  set is already correct and cheap (#244 calls this "low-value on its own").
- Live-token confirmation of the exact payload shrink — covered by existing `SkippableFact`
  integration; request-building is asserted offline.

## Acceptance criteria (from the issue)
- Optional, config-backed `date_updated_gt` window threaded into the feed's assigned-tasks fetch.
- Interaction with the comment fan-out and F12 confirmed (analysis above).
- Default-off preserves current behavior; `dotnet test` green (integration `SkippableFact`).
