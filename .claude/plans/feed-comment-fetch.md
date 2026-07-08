# Plan: Comment fetch in the API facade (#111)

Part of the Mentions & Comments feed epic (#109). Foundation for the aggregation
service (#112), which fans comment fetches out across assigned tasks and must know
which task each comment belongs to; and for "open task from a feed entry" (#115).

## Current state (already in the repo)

The detail-view Comments tab (#17) already landed:

- `CommentItem(string Id, string Author, long? DateMs, string Text, bool Resolved)`
  in `ClickUp/Models.cs`.
- `ClickUpClient.GetTaskCommentsAsync(taskId)` — maps the already-generated
  `V2.Task[id].Comment.GetAsync` (`/v2/task/{task_id}/comment`) into `CommentItem`s
  through the `Guard(...)` wrapper.
- `TaskService.GetTaskCommentsAsync` delegates to the client.

So the record and the fetch exist — but neither serves the **feed's** needs:

1. **No task attribution.** A `CommentItem` carries no reference to the task it
   belongs to. The aggregation service (#112) merges comments from many tasks into
   one feed and needs to attribute (and later open, #115) each one.
2. **No tested mapping seam.** The mapping is done inline in `GetTaskCommentsAsync`,
   so it has no offline unit coverage (unlike `Map`/`MapDetail`, which are
   `internal static` and tested in `ClickUpClientMapTests`).

## Scope of this issue

- **Add a task reference to `CommentItem`.** Append an optional trailing
  `string? TaskId = null` so the change is source-compatible with every existing
  call site (detail formatter, detail screen, agent dispatch/composer, and the
  positional constructions in the tests). The detail view simply ignores it.
- **Extract an `internal static MapComment(Comment, string? taskId)` seam** in
  `ClickUpClient`, mirroring `Map`/`MapDetail`, reusing the existing `DisplayName`
  (username → email → id fallback) and `ParseMs` helpers. `GetTaskCommentsAsync`
  stamps the requested `taskId` onto each mapped comment.
- **Unit-test `MapComment`** offline (mirroring `ClickUpClientMapTests`): author
  fallback, epoch-ms parse, resolved flag, task attribution, and null/missing-field
  degradation.

## Explicitly deferred (not in scope here)

- **De-paging.** The issue text mentions the `PageAsync` de-paging helper, but that
  helper is task-typed (returns `List<TaskItem>` from `TasksResponse`) and, more
  fundamentally, the generated comment endpoint exposes **no pagination query
  parameters** (`CommentRequestBuilder.GetAsync` takes only `DefaultQueryParameters`;
  the real ClickUp API paginates comments by a `start`/`start_id` cursor that the
  curated spec doesn't model). Wiring true de-paging would require editing
  `clickup-openapi.json` and regenerating the Kiota client — which #111 explicitly
  rules out ("No Kiota regeneration"). So this ships the single-page fetch (ClickUp
  returns most-recent-first) and defers cursor de-paging to a follow-up issue,
  linked from the PR.

## Non-goals (later issues)

- Aggregation/merge across tasks — #112.
- Mention detection/filtering — #113.
- Rendering the feed — #114.

## Tests / verification

- New `ClickUpClientCommentMapTests` covering `MapComment` offline.
- `dotnet build -c Release` (0/0) and `dotnet test -c Release` green (integration
  tests skip without `CLICKUP_TOKEN`). No TUI surface touched.
