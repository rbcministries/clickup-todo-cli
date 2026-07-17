# Feed "Recent activity" source via `date_updated` (#117)

Part of the Mentions & Comments feed epic (#109). Optional-stretch, greenlit by the
maintainer on 2026-07-15:

> Let's implement this, with the results displayed only when a "display non-comment
> activity" flag / display state is set. We can use **F6** to toggle this state on and
> off (not tied to main list screen's F6-bound state, help text can be something like
> "show/hide activity").

## Acceptance criteria (from the issue)

- Recently-updated assigned tasks appear **in** the feed, **newest-first**, gated behind an
  **F6** "show/hide activity" display state (default off).
- `dotnet test` green; `tui-validate` covers the combined view.

## Key insight — activity is free & F6 is a local filter

`FeedService.LoadFeedAsync` **already fetches the assigned tasks** (it needs their ids to
fan comment fetches out over them). `TaskItem.UpdatedMs` already carries ClickUp
`date_updated`. So the "recent activity" set is a pure client-side **projection of tasks
already in hand** — no extra endpoint, no new query params.

Because the tasks are already loaded, **F6 is a display-only re-render** (like F3
mentions-only), *not* a re-fetch (unlike F12, which changes `include_closed` and thus what
the server returns). F12 still governs whether *completed* tasks are in the fetched set, so
it transitively bounds activity too — consistent with comments.

## Design

### Phase 1 — domain + service (pure, unit-tested)

- **`ClickUp/Models.cs`**: add `ActivityItem(string Id, string TaskId, string TaskName,
  string? StatusName, string? StatusColor, long? UpdatedMs)`. `Id = "activity:" + TaskId`
  so it never collides with a comment id in de-dup / selection.
- **`Services/FeedService.cs`**:
  - Introduce `FeedResult(IReadOnlyList<CommentItem> Comments, IReadOnlyList<ActivityItem> Activity)`.
  - Change `LoadFeedAsync` to return `FeedResult` (no existing *test* calls it — only the app;
    the tests pin the pure statics). It projects the `tasks` it already fetched via a new pure
    `internal static BuildActivity(tasks, maxEntries)`: drop id-less tasks, newest-first by
    `UpdatedMs` (null last), ties by `TaskId` ordinal, cap to `DefaultMaxEntries`.
- **`Tui/FeedRowFormatter.cs`**: add `Format(ActivityItem)` → a `Row` whose leading gutter is a
  distinct-coloured activity chip (` ~ `, fixed accent `ActivityBadgeColor`), then task name ·
  updated date · status. `SearchKey` = task name. Reuses the existing leading-chip-span
  mechanism (the renderer colours the span per row-kind).

### Phase 2 — config, screen, host

- **`Configuration/AppConfig.cs`**: `bool FeedShowActivity` (default false; persisted like
  `FeedShowCompleted`). **Not** part of `FeedCache.KeyFor` — it's display-only, doesn't change
  what's fetched.
- **`Tui/Screens/FeedEntry.cs`** (new): a unified display row — either a `CommentItem` or an
  `ActivityItem` — carrying `Id`, `DateMs`, `TaskId`, `MentionsMe`, `IsActivity`. Static
  `Of(comment)` / `Of(activity)` factories.
- **`Tui/Screens/NotificationsFeedScreen.cs`**: hold `_comments` + `_activity` + `_showActivity`.
  - New pure `BuildEntries(comments, activity, mentionsOnly, showActivity)`: comments filtered by
    mentions-only, activity appended **only when `showActivity && !mentionsOnly`** (mentions-only
    is the narrowest view — task activity isn't a mention), merged newest-first (ties by id).
  - `BuildRows` and selection helpers move onto `FeedEntry`. `TitleFor` gains a `showActivity`
    arg → ` (+activity)` suffix (only when shown). F6 → `ToggleActivityRequested`; host flips +
    persists + `SetShowActivity` (which re-renders locally — no re-fetch).
  - `UpdateFeed(FeedResult)` swaps both lists; selection still follows the same entry id.
- **`Tui/Screens/HelpLine.cs`**: add `new("F6", "activity")` to `HelpItemSets.NotificationsFeed`.
- **`Tui/TodoApp.cs`**: pass `result.Activity` + `_config.FeedShowActivity` to the screen; cache
  `result.Comments` only (cache shape unchanged — activity re-derives on the near-immediate live
  refresh that both cold and warm opens trigger); add `ToggleFeedShowActivity`; feed `result`
  through `UpdateFeed`.

### Phase 3 — validation

- `tui-validate` drive script `activity_check.py`: open feed (Ctrl+E), assert task titles are
  **absent** (comments-only), press **F6**, assert a seeded task title now appears and the title
  shows `+activity`, press F6 again → drops back out. Plus the A/B latency + colour + `feed_check`
  non-regression the skill requires.
- README: one line documenting F6 in the feed.

## Invariants preserved

- **No `Generated/` / curated-spec / Kiota change** — reuses `GetAssignedTasksAsync` + existing
  `TaskItem.UpdatedMs`; no new API surface.
- **No second focusable pane (#3/#38)** — activity rows render in the existing single `ListView`
  via the `_rows` mechanism.
- **No bare-letter keybinding (#12)** — F6 is a function key, mirroring the main list's F6.
- Personal-token raw `Authorization` header untouched; integration tests stay `SkippableFact`.

## Deferred (tracked)

- Server-side `order_by=updated` / `date_updated_gt` narrowing of the activity fetch — an
  optimisation over the current client-side sort of the already-fetched bounded set; not needed to
  satisfy the AC. Will file a follow-up issue and link it from the PR.
- Persisting activity in `FeedCache` (so it survives the instant-paint gap before the live
  refresh) — minor; the refresh fills it within moments.
