# Mention detection: structured user-id matching via comment blocks (#167)

Follow-up to #113 (text-based `@handle` detection, on `main`). Add **id-based** mention
detection so a comment that @-mentions the signed-in user by their numeric ClickUp id is
flagged even when the rendered `@handle` text differs from our derived handles.

## Background

- `#113` matches an `@`-prefixed handle in the flattened `comment_text` against the user's
  `DisplayName` (+ email local part). Reliable, but blind to the mentioned user's numeric id.
- ClickUp's `GET /v2/task/{id}/comment` returns, alongside the flat `comment_text`, a
  structured `comment` **array of blocks**. A plain run is `{ "text": "…" }`; an @-mention run
  is `{ "text": "@Name", "type": "tag", "user": { "id": 183, … } }`. The curated spec only
  models `comment_text`, so the blocks aren't reachable without a spec edit + Kiota regen.

## Acceptance criteria (from the issue)

1. Extend the curated `Comment` schema with the structured `comment` blocks array (mention
   blocks carrying the referenced user id); regen the client (never hand-edit `Generated/`).
2. Map the mentioned user ids into a new `CommentItem.MentionedUserIds` via
   `ClickUpClient.MapComment`.
3. Extend `MentionDetector` to also flag a mention when the authenticated user's id
   (`GetMeAsync`) ∈ `MentionedUserIds`, in addition to the existing `@handle` text match.
   Keep the pure/offline-testable shape.
4. Add fixtures/tests for the structured-block payload shape.

## Design

### Phase 1 — spec + regen (no hand-edits to `Generated/`)

`src/ClickUpTodo/ClickUp/clickup-openapi.json`:

- New `CommentBlock` schema: `{ text?: string, type?: string, user?: $ref User }` (User already
  carries `id: int64`).
- Add to `Comment`: `"comment": { type: array, nullable, items: $ref CommentBlock }`.

Regenerate with the repo's pinned Kiota tool. `scripts/regen-client.ps1` is a thin wrapper over
`dotnet kiota generate …`; no `pwsh` in this env, so run the underlying command with the script's
exact args. Expect a new `CommentBlock.cs` model and a `CommentProp` (Kiota mangles the `comment`
property because it collides with the class name `Comment`, mirroring `StatusProp`/`PriorityProp`).

### Phase 2 — domain mapping + detection + tests

- `CommentItem` (Models.cs): add positional param `IReadOnlyList<long>? MentionedUserIds = null`
  with a body property normalizing `?? []` (the established `Options ?? []` pattern). Existing
  positional constructions and `with { MentionsMe = … }` stay valid.
- `ClickUpClient.MapComment`: populate `MentionedUserIds` from the blocks —
  `c.CommentProp?.Where(b => b.User?.Id is > 0).Select(…).Distinct().ToList() ?? []`.
- `MentionSpec` (MentionDetector.cs): add `long? UserId`. `ForUser` sets it from `user.Id`
  (only when `> 0`); `None` leaves it null.
- `MentionDetector.Mentions(CommentItem, spec)`: return `MentionsById(comment, spec) ||
  Mentions(comment.Text, spec)`. `MentionsById` = `spec.UserId is { } id && ids.Contains(id)`.
  The pure text overload `Mentions(string?, spec)` is unchanged.
- `FeedService.StampMentions` already calls the `CommentItem` overload, so the feed gets id
  matching for free — no FeedService change needed (verify with a test).

### Tests

- `ClickUpClientCommentMapTests`: a `Comment` with mention blocks → `MentionedUserIds` populated
  (dedup, ignores blocks without a user / with id 0, plain-text blocks contribute nothing);
  absent `comment` array → empty list; existing field/attribution tests still pass.
- `MentionDetectorTests`: id match with no text handle match; `None`/null-UserId never id-matches;
  id 0 guarded; text path unchanged; `MentionSpec.ForUser` carries the id.
- `FeedServiceTests`: `StampMentions` flags an entry by id alone (no `@handle` in text).

## Out of scope (unchanged from #113 / API limits)

Comment mentions only; no workspace-wide mention discovery; docs excluded. This only makes
in-scope comment detection more precise (id-based, robust to display-name rendering).

## Verification

`dotnet build -c Release` (0/0) → `dotnet test -c Release` green (integration self-skips). No TUI
surface changes (pure domain/mapping), so `tui-validate` is not required for this slice; the feed
render path is unchanged.
