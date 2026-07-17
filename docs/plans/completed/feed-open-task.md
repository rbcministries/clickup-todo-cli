# Open the task from a feed entry (#115)

Part of the Mentions & Comments feed epic (#109). Makes the feed **actionable**:
pressing **Enter** on a feed row opens the underlying task's `TaskDetailScreen`,
and closing it returns to the feed with the selection preserved.

## Current state (verified)

- `NotificationsFeedScreen` (`src/ClickUpTodo/Tui/Screens/NotificationsFeedScreen.cs`)
  is a single-focusable `ListView` on the shared screen seam. It renders the
  already-fetched feed and handles F3 (mentions-only), F1 (help), Esc (back).
  There is **no** Enter handling today.
- Each feed `CommentItem` is stamped with its `TaskId`
  (`ClickUpClient.GetTaskCommentsAsync` maps `TaskId: taskId`, `ClickUpClient.cs:351`;
  `FeedService` preserves it), so a row already knows which task it belongs to.
- The screen stack (`TodoApp.ShowScreen`/`CloseScreen`) already supports
  **stacking**: opening a screen hides the layer beneath (kept mounted) and Esc
  restores it — `below.Visible = true; below.SetFocus()` — with its `ListView`
  selection intact. This is exactly the feed → detail → back flow.
- `TodoApp.OpenDetail()` fetches detail + comments off the UI thread and mounts a
  `TaskDetailScreen` via `ShowScreen`. It bails when `ActiveScreen is not null`,
  so it can't be reused verbatim from the feed (the feed *is* the active screen).
- `HelpItemSets.NotificationsFeed` (`HelpLine.cs:162`) has a comment noting the
  open-task item "arrives with #115".

## Design

Reuse the existing screen stack — no new focusable panes (keeps the #3/#38
latency invariants). The feed raises an event; the host stacks the detail screen
on top of the feed.

### 1. `NotificationsFeedScreen` — raise an open-task request on Enter
- Add `public event EventHandler<string>? OpenTaskRequested;` (payload = task id).
- Store the currently-displayed (filtered) rows in a `_rows` field, set in
  `RenderFeed`, so Enter maps `_list.SelectedItem` → `CommentItem` exactly as
  displayed (honours the F3 filter).
- `KeyCode.Enter`: resolve the selected row's task id; if present, raise
  `OpenTaskRequested`; if the selected comment has no task id, flash a note; if
  there's no selection/empty feed, do nothing.
- Pure, unit-testable core: `internal static string? SelectedTaskId(rows, index)`
  returns the row's non-empty `TaskId` or null (out-of-range / empty id → null).

### 2. `TodoApp` — a reusable stackable detail open
- Extract the fetch-and-mount body of `OpenDetail()` into
  `OpenTaskDetail(string taskId)`. Capture `requester = ActiveScreen` at call
  time and, in the post-fetch `Application.Invoke`, only mount when
  `ActiveScreen == requester`. This one guard serves both callers:
  - from the list, `requester` is `null` ⇒ mount only if still idle (matches the
    old `ActiveScreen is not null` re-check, and blocks a double-open);
  - from the feed, `requester` is the feed ⇒ mount stacked on it, and a second
    Enter (after the first detail mounts) is a no-op because `ActiveScreen` is
    then the detail screen.
- `OpenDetail()` keeps its `task is null || ActiveScreen is not null` entry guard
  and delegates to `OpenTaskDetail(task.Id)`.
- In `OpenNotificationsFeed()`, wire `screen.OpenTaskRequested += (_, id) =>
  OpenTaskDetail(id);` before `ShowScreen`.

### 3. Help / footer copy
- Add `new("Enter", "open")` to `HelpItemSets.NotificationsFeed`.
- Update the `HelpScreen` F5 line to mention Enter opens the task.

## Tests

- **`NotificationsFeedScreenTests`**: `SelectedTaskId` returns the id for a valid
  row, null for out-of-range indices, null for a row with a null/empty `TaskId`,
  and (composed with `Filter`) resolves against the *filtered* rows under
  mentions-only.
- **`HelpLineTests`**: update the pinned `NotificationsFeed` footer string to
  include `Enter open`.

## Manual / TUI validation

`tui-validate` PTY probe (after `dotnet test` is green): F5 opens the feed, Enter
on a row opens that task's detail, Esc returns to the feed at the same row, and
the list/detail A/B renders stay byte-identical to the stock renderer.

## Out of scope (tracked elsewhere)

- Background-refresh integration for the feed — #116.
- "Recent activity" via `date_updated` — #117.
