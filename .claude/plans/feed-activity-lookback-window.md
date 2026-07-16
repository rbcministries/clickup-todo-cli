# Feed activity: narrow the assigned-tasks fetch server-side via `date_updated_gt` (#244)

Follow-up deferred from #117 (the F6 "recent activity" source). #117 shipped the recent-activity
source as a **pure client-side projection** of the assigned tasks the feed already fetches — no new
API surface. This issue adds the deferred **server-side narrowing**: an opt-in look-back window that
threads `date_updated_gt` into the feed's assigned-tasks fetch so a busy workspace fetches fewer
tasks.

## Acceptance criteria (from the issue)

- An optional, config-backed `date_updated_gt` window is threaded into the feed's assigned-tasks
  fetch so a busy workspace fetches fewer tasks.
- The interaction with the comment fan-out (same task set feeds both) and with the F12 completed
  toggle is confirmed and documented.
- Low priority / optimisation only — must be a **no-op by default** (today's behavior unchanged).
- `dotnet test` green; integration stays `SkippableFact`.

## Key facts (verified in the code)

- `FeedService.LoadFeedAsync` fetches assigned tasks **once** via
  `ClickUpClient.GetAssignedTasksAsync(workspaceId, assigneeIds, includeClosed, ct)` and uses that one
  set for **both** the comment fan-out (`GatherAsync` over the task ids) and the recent-activity
  projection (`BuildActivity`). So narrowing the fetch narrows both — this is the documented
  trade-off, not a bug.
- The generated client already exposes the `date_updated_gt` query param: the delta path (#194)
  sets `cfg.QueryParameters.DateUpdatedGt = updatedAfterMs` in `GetAssignedTasksDeltaAsync`. So **no
  spec edit / no Kiota regen** is needed — we reuse the existing query parameter.
- `TimeProvider` is the repo's injectable clock (e.g. `FeedCache` takes `TimeProvider? timeProvider`
  defaulting to `TimeProvider.System`, reads `GetUtcNow().ToUnixTimeMilliseconds()`). `FeedService`
  does not take one yet.
- `FeedCache.KeyFor` fingerprints workspace + `FeedShowCompleted` + assignee-rule values.

## Design decisions

### Standalone config setting, **not** gated on the F6 activity flag
The issue phrase "when the activity source is on" could read as gating the fetch on `FeedShowActivity`
(F6). We deliberately **do not** do that: #117 made `FeedShowActivity` a display-only re-render that
is **excluded from `FeedCache.KeyFor`** and does **not** trigger a re-fetch. Gating the fetch on it
would break that invariant (toggling F6 would suddenly need a re-fetch). Instead the look-back is its
own opt-in setting, independent of F6, that changes what is fetched whenever it is > 0.

### `FeedActivityLookbackDays` (int, default 0 = disabled)
`0` (the default for every existing config, no migration) means "no window" → today's behavior
byte-for-byte. A positive `N` narrows the fetch to tasks updated in the last `N` days.

### Kept **out** of `FeedCache.KeyFor`
The window is **time-relative** (`now − N days`), so it shifts every second even with config held
constant — putting it in the cache key would make the key perpetually unstable (never a hit) for no
benefit. The cache is a bounded, best-effort instant-paint that the near-immediate live refresh both
cold and warm opens trigger reconciles within moments — the same rationale #117 used to keep activity
out of the cached payload. Worst case on a toggle: the instant-paint shows a slightly wider/narrower
set for a beat until the refresh corrects it; never a *wrong* feed (workspace/assignees/completed are
still keyed).

### Interactions (confirmed, documented in code)
- **Comment fan-out:** narrowing the shared fetch narrows the tasks whose comments are fetched. But a
  new comment bumps a task's `date_updated`, so a task with recent comment activity **stays in
  window**. The window therefore means "comments/activity from tasks touched in the last N days" — a
  coherent, user-chosen semantic. Comments on otherwise-stale tasks drop out; that's the opt-in
  trade-off.
- **F12 `include_closed`:** orthogonal — `date_updated_gt` and `include_closed` compose exactly as
  they already do on the proven delta path (`GetAssignedTasksDeltaAsync` sets both). The look-back
  narrows *by time*; F12 governs *whether closed tasks* are returned.

## Phases

### Phase 1 — client param + config + pure window (unit-tested)
- `ClickUp/IClickUpClient.cs` + `ClickUp/ClickUpClient.cs`: add an optional `long? updatedAfterMs = null`
  to `GetAssignedTasksAsync`. When non-null, set `cfg.QueryParameters.DateUpdatedGt = ms`; when null,
  the param is never set → identical request to today. (Mirrors the delta path's use of the same
  query parameter.)
- `Configuration/AppConfig.cs`: `int FeedActivityLookbackDays { get; set; }` (default 0), with an XML
  doc capturing the opt-in/no-op-by-default semantics, the retained-recent-comments behavior, the F12
  orthogonality, and the deliberate exclusion from `FeedCache.KeyFor`.
- `Services/FeedService.cs`: pure `internal static long? ActivityLookbackWindowMs(int lookbackDays,
  DateTimeOffset now)` → `null` when `lookbackDays <= 0`, else `(now − TimeSpan.FromDays(lookbackDays))
  .ToUnixTimeMilliseconds()`. Unit-tested.
- Tests: `ClickUpClientAssignedTasksTests.cs` (new) drives the real generated client through a
  capturing `HttpMessageHandler`, asserting `date_updated_gt` **is** on the outgoing URI when a window
  is passed and **absent** when it is null; `FeedServiceTests` gains `ActivityLookbackWindowMs` cases.

### Phase 2 — wire into the feed load
- `Services/FeedService.cs`: add `TimeProvider? timeProvider = null` to the ctor (→
  `_clock = timeProvider ?? TimeProvider.System`). In `LoadFeedAsync`, compute the window from
  `config.FeedActivityLookbackDays` against `_clock.GetUtcNow()` and pass it to `GetAssignedTasksAsync`.
  Caller signature (`LoadFeedAsync(includeClosed, mentionsOnly, ct)`) unchanged — the look-back isn't a
  cache-key input, so it needn't be captured on the UI thread like `includeClosed` is.
- `README.md`: one line documenting the opt-in `FeedActivityLookbackDays` config value.

## Deferred (tracked, linked from the PR)
- **F2 settings-screen field** for `FeedActivityLookbackDays`. The settings dialog
  (`Tui/Screens/SettingsScreen.cs`) is a tightly-packed absolute-positioned Terminal.Gui layout;
  adding a field means repositioning siblings — a non-CI-testable TUI change disproportionate to this
  opt-in optimisation. The value is functional via `config.json` today. A follow-up issue will add the
  field (plus a `SettingsForm.ParseActivityLookbackDays` pure parser + `SettingsFormTests`).

## Invariants preserved
- **No `Generated/` / curated-spec / Kiota change** — reuses the existing `date_updated_gt` query
  parameter the delta path already uses.
- **No TUI surface** in this slice (no second focusable pane, no bare-letter keybinding concerns).
- Personal-token raw `Authorization` header untouched; integration tests stay `SkippableFact`.
- **No-op by default** (`FeedActivityLookbackDays = 0`) — existing behavior is byte-for-byte unchanged.
