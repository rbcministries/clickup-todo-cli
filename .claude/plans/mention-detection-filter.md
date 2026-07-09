# Mention detection & filter (#113)

Part of the Mentions & Comments feed epic (#109). Builds on the merged feed **aggregation
service** (#112, `FeedService`). Adds the detection layer that flags which feed entries mention
the current user, plus a "mentions only" filter the feed screen (#114) will bind to. **No screen
rendering** — the feed screen is still the empty-state scaffold (#110); real rows land in #114.
This slice ships the detection + filter logic + its unit tests, mirroring how #112 shipped the
service without UI wiring.

## Acceptance criteria (from the issue)

- Mention flag is set correctly for comments that reference the current user and not for others
  (unit-tested with representative payloads).
- A "mentions only" toggle filters the feed list to mentions only; `dotnet test` green.

## Substrate & scope decision

`CommentItem` (the stable feed record) carries the flattened `comment_text` as `Text` — the whole
feed is built on this. The generated `Comment` type surfaces only `comment_text` / `user` /
`date` / `id` / `resolved`; the structured `comment` **blocks** (which would carry a mentioned
user's numeric id) are **not** in the curated OpenAPI spec, so they aren't available without a
spec edit + Kiota regen. The fake backend and integration fixtures likewise use flat
`comment_text`.

Detection therefore operates on `comment_text`, matching an **`@`-prefixed handle** against the
authenticated user's identity (`GetMeAsync` → `ClickUpUser.DisplayName`, which is the
username → email → id the mapper already resolves). Requiring the `@` prefix avoids false
positives from a name appearing in prose ("Ben looks good" is not a mention). This satisfies the
issue's acceptance criteria and aligns with its stated **API-imposed limitations** (comment
mentions only; no workspace-wide discovery).

Reliable **structured user-id** matching (parsing the `comment` blocks for a mentioned id) needs
the spec enrichment + regen and is tracked as a follow-up (see *Deferred*).

## Design

### Model — `CommentItem` gains `bool MentionsMe = false`
A trailing optional positional field on `ClickUp/Models.cs`'s `CommentItem`. Default `false` keeps
every existing construction and `ClickUpClient.MapComment` unchanged (record value-equality tests
compare two defaults, so they still pass). The flag lives on the entry so #114 can highlight
mentions and the filter can select them.

### New pure component — `Services/MentionDetector.cs`
- **`MentionSpec`** — the normalized handle tokens to match, built from the current user.
  - `MentionSpec.ForUser(ClickUpUser user)` → tokens from `DisplayName` (trimmed, lowercased).
    If the display name is an email (`ben@odb.org`), also add the **local part** (`ben`) so
    `@ben` matches. Blank tokens are dropped; a user with no usable token yields a spec that never
    matches.
- **`Mentions(string? commentText, MentionSpec spec)`** — pure. For each token, looks for `@{token}`
  case-insensitively, requiring a non-word boundary after the match so `@Benny` does not match the
  token `ben` (multi-word display names like `Ben Seymour` match as `@Ben Seymour`). Returns true if
  any token matches.
- **`Mentions(CommentItem comment, MentionSpec spec)`** — convenience overload over `comment.Text`.

### `FeedService`
- **`StampMentions(feed, spec, mentionsOnly)`** — `internal static`, pure: returns each entry with
  `MentionsMe` set via the detector; when `mentionsOnly` is true, filters to mentioned entries
  (order preserved). Unit-testable offline via `InternalsVisibleTo`.
- **`LoadFeedAsync(bool mentionsOnly = false, ct)`** — gathers the feed as today, resolves the
  current user once (memoized in a `_currentUser` field so #116's background refresh doesn't
  re-fetch every tick), builds the spec, and returns `StampMentions(...)`. The existing zero-arg
  call sites (none yet) get `mentionsOnly = false` by default. `GetMeAsync` errors propagate, like
  the existing `GetAssignedTasksAsync` call (it's the token-validation call — if it fails nothing
  works).

## Tests

`tests/ClickUpTodo.Tests/MentionDetectorTests.cs`:
- `@Ben Seymour` in text, spec for `Ben Seymour` → mention; plain `Ben looks good` (no `@`) → not.
- Boundary: token `Ben`, text `@Benny` → not a mention.
- Case-insensitive (`@ben seymour`).
- Email display name `ben@odb.org`: `@ben` matches (local part) and `@ben@odb.org` matches.
- Blank / whitespace display name → spec matches nothing.
- `@`-mention of a different handle → not a mention.
- Null / empty comment text → not a mention.

`tests/ClickUpTodo.Tests/FeedServiceTests.cs` (additions):
- `StampMentions` sets `MentionsMe` true/false per entry against a spec.
- `mentionsOnly: true` filters to mentioned entries only, order preserved.
- `mentionsOnly: false` returns all entries, stamped.
- Empty feed → empty.

## Deferred / follow-ups

- **Structured user-id mention matching** (parse the `comment` blocks for a mentioned numeric id):
  needs the curated spec's `Comment` schema extended with the blocks array + Kiota regen + mapping
  a `MentionedUserIds` set onto `CommentItem`. Filed as a follow-up issue and linked from the PR.
- **Screen wiring** (an F-key "mentions only" toggle rendering the filtered list) belongs to the
  feed-render issue **#114** — this slice ships the filter the screen binds to, matching #112's
  non-UI split.

## Phases

1. Plan (this doc) + `MentionsMe` field + `MentionDetector` + tests; build/test/format; open draft PR.
2. `FeedService.StampMentions` + `LoadFeedAsync` filter + tests; build/test/format; push.
