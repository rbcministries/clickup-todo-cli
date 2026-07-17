# Render real feed data in the screen (#114)

Part of the Mentions & Comments feed epic (#109). Its dependencies are all landed on `main`:
the feed screen **scaffold** (#110, `NotificationsFeedScreen`), the **aggregation service** (#112,
`FeedService`), and **mention detection + the mentions-only filter** (#113, `MentionDetector` /
`FeedService.StampMentions` / `LoadFeedAsync(mentionsOnly)`). This slice replaces the scaffold's
static placeholder with live rows.

## Acceptance criteria (from the issue)

- Live feed renders with correct colors/badges and loading/empty/error states.
- A feed-row formatter drives rows via `StatusBadgeListSource` (parallel text + badge spans + search
  keys, per `Tui/StatusBadgeListSource.cs`).
- Fetch happens off the UI thread like `OpenDetail()` (`Flash("Loading…")` → `Task.Run` →
  `Application.Invoke`).
- Mentions stand out (a mention badge), and the "mentions only" toggle from #113 is bound to a
  keypress.
- `tui-validate` asserts rows render, the mention badge appears, the mentions-only toggle works, and
  keypress latency is within budget — run only after `dotnet test` is green.

## Design

### New pure formatter — `Tui/FeedRowFormatter.cs`

Mirrors `TaskRowFormatter`: pure (no Terminal.Gui), returns the one-line display text plus the char
span of the mention badge and a decoupled type-ahead search key, so layout + spans are unit-tested.

`Row(string Text, int MentionStart, int MentionLength, string SearchKey)`.

Row text: `{gutter}{author}{  ·  }{date}{  ·  }{preview}`

- **Gutter** (3 display columns, matching `TaskRowFormatter.StatusIcon`/`BlankGutter` so authors line
  up): a coloured ` @ ` chip when `CommentItem.MentionsMe`, else a blank `   ` gutter. The chip's
  char span `(0, 3)` is reported as `MentionStart`/`MentionLength`; a non-mention reports `(-1, 0)`
  (the "no badge" sentinel, same convention as `TaskRowFormatter`).
- **Author** — `CommentItem.Author`, trimmed; `(unknown)` when blank.
- **Date** — `MMM d, HH:mm` local, omitted (with its separator) when `DateMs` is null.
- **Preview** — `CommentItem.Text` flattened to a single line (whitespace/newlines collapsed to one
  space) and truncated to 100 chars + `…`; `(empty comment)` when blank.
- **SearchKey** — the author (title analog), so type-ahead jumps by author even though the rendered
  line leads with the gutter (same decoupling `TaskRowFormatter`/#76 use for titles).

The badge attribute is built in the view layer (not the pure formatter) via
`StatusBadgeListSource.TryCreate(start, length, FeedRowFormatter.MentionBadgeColor)`, mirroring how
`TodoApp.BuildRow` colours task badges from a hex string. `MentionBadgeColor` is a fixed amber accent
(deliberately not a ClickUp field colour) so a mention reads as "you were mentioned".

### `NotificationsFeedScreen` becomes data-bearing

Constructor takes the already-fetched, already-stamped feed:
`NotificationsFeedScreen(IReadOnlyList<CommentItem> feed, bool mentionsOnly = false)`.

- A single focusable `ListView` fills the body, its `Source` a `StatusBadgeListSource` built from the
  (filtered) feed via `BuildRows` (parallel `text` / `badges` / `searchKeys`). No second focusable
  pane — the single-ListView invariant (#3) holds, same as the status picker.
- An overlaid `Label` shows the empty-state copy when the (filtered) feed is empty; hidden otherwise.
- Key handling on the `ListView` (mirrors `StatusPickerScreen`): `F1` → help, `Esc` → close, **`F3`**
  → toggle mentions-only (re-filter + rebuild rows in place, update the Title, flash the new state).
  `↑/↓` and type-ahead are the ListView's own.
- Pure, CI-testable statics: `Filter(feed, mentionsOnly)`, `EmptyMessage(mentionsOnly, hasAny)`, and
  `BuildRows` (asserts a mention row carries a badge span and a plain row does not — the "mention
  badge appears" criterion in a CI-testable form).

**Toggle key — `F3`.** Rationale: `F3` is the app's "filter" key (it opens filter/sort/group on the
list), and mentions-only *is* a filter; F-key toggles are already idiomatic here (F4 subtasks, F6
badges). The always-visible footer labels it (`F3 mentions only`), so there's no hidden surprise.
Trivially changeable by the maintainer — noted in the PR.

**Loading / error states** follow the established `OpenDetail()` pattern: the host flashes
`Loading feed…` before the fetch and `Could not load feed: …` on failure, and only constructs the
screen on success. The **empty** state is the screen's placeholder (distinct copy for
"no comments at all" vs "no mentions — F3 shows all"). This matches how the detail screen only
exists on a successful load.

### Host wiring — `TodoApp` / `Program` / harness

- `TodoApp` gains a `FeedService` (new ctor param). `OpenNotificationsFeed()` now flashes
  `Loading feed…`, fetches `LoadFeedAsync(mentionsOnly: false)` off-thread, and shows the
  data-bearing screen on the UI thread — guarded on `ActiveScreen` like every other open. Loading the
  full feed once (all entries stamped) lets the `F3` toggle filter **locally** with no re-fetch, so
  it's instant.
- `Program.cs` constructs `new FeedService(client, taskService, config)` and passes it in.
- The `tui-validate` harness `Program.cs` constructs `FeedService` the same way; its fake backend
  gains one comment that `@`-mentions the current user so the mention badge + `F3` filter can be
  validated end-to-end.

### Help surfaces

- `HelpItemSets.NotificationsFeed` → `↑/↓ move · F3 mentions only · F1 help · Esc back` (keeps the
  screen-set invariants: non-empty, offers F1 help, ends with Esc).
- `HelpScreen` F5 line gains a note that `F3` in the feed shows mentions only.

## Tests

- `FeedRowFormatterTests` (new): mention vs non-mention gutter + span; author fallback; date present
  vs absent; preview flattening + truncation + empty fallback; search key = author.
- `NotificationsFeedScreenTests` (extend): `Filter` selects mentions / passes all through;
  `EmptyMessage` picks the right copy; `BuildRows` attaches a badge only to mention rows; the updated
  `EmptyStatePlaceholder` still names mentions/comments + Esc; `HelpItems` is the feed set.
- `HelpLineTests`: update the pinned `NotificationsFeed` footer to the new set.
- Terminal.Gui view glue (ListView, focus, F3 rebuild) is not CI-testable — verified by build +
  `tui-validate`.

## TUI validation (after `dotnet test` is green)

`tui-validate`: boot → `F5` (feed) → assert rows render with the author/date/preview shape and the
amber mention chip on the mentioned row → `F3` → assert the list narrows to the mention → `F3` again
→ assert it widens back → `Esc` → dashboard restored, cursor intact. Confirm keypress latency and
per-press output volume are within budget and no second focusable pane was added (#3).

## Phases

1. Plan (this doc) + `FeedRowFormatter` + unit tests. Build/test/format; open draft PR.
2. Data-bearing `NotificationsFeedScreen` + `TodoApp`/`Program`/harness wiring + help updates +
   tests. Build/test/format; push.
3. `tui-validate` (harness mention comment + feed drive script); PR notes; mark ready.

## Out of scope (later issues in #109)

- Open the task from a feed entry (#115), background-refresh integration + manual refresh key (#116),
  the optional `date_updated` "recent activity" source (#117), persistent feed cache (#123),
  structured user-id mention matching (#167).
