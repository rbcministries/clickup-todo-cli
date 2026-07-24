# Spike (G): structured comment / description write payload + @Brain feasibility

Issue: #321 (part of epic #313 — Task Detail & Comments UX). **Investigation-forward
spike, no shipping code.** Foundation for **H** (#322, comment structured-block write
path) and **L** (#326, description @-mention + structured description write).

Its job: nail down, *before* building, exactly how ClickUp's public **v2** API accepts a
written @-mention in (a) a comment and (b) a task description, and whether **@Brain /
named Super Agents** can be tagged at all — then hand H/L the precise `clickup-openapi.json`
deltas + regen plan they will execute.

## Method & evidence (and its limits)

The authoritative page — ClickUp's [`comment-formatting`](https://developer.clickup.com/docs/comment-formatting)
guide — and the whole `developer.clickup.com` host are **blocked by this session's egress
policy** (HTTP 403 at the proxy), so the findings below are triangulated from:

1. **ClickUp's own documented example** of the comment-blocks payload, surfaced verbatim
   through web search of the comment-formatting guide (quoted below).
2. **The repo's existing read-side model** — the `CommentBlock` schema and `MapComment` /
   `MapMentionedUserIds` mapping added for id-based mention *detection* (#167), which already
   proves the persisted block shape `{ text, type:"tag", user:{ id } }`.
3. **The official v2 OpenAPI reference** for `POST /task/{task_id}/comment` (read via a public
   mirror of `clickup20.json`), which documents only `comment_text` / `assignee` / `notify_all`
   — i.e. the structured blocks array is a **real but spec-undocumented** capability.
4. **A live read of this workspace** via the ClickUp MCP: the full `GET /team` member list
   (535 members) — used to settle the @Brain / Super-Agent question empirically.

**Confidence.** The comment-mention write shape (Finding 1) and the @Brain verdict
(Finding 3) are **high confidence** — the first is ClickUp's own published example matched
to our read-side model, the second is a direct observation of the members endpoint. The
description verdict (Finding 2) is **high confidence as a documented-surface no-go** but its
"could a bare `@` in markdown ever link" corner is the one thing worth a **single live-write
probe** before L is finalized (see Finding 2). No throwaway probe code was written or merged
for this spike; the one write test that would remove the last bit of doubt is called out for
H/L to run under `CLICKUP_TOKEN`.

---

## Finding 1 — Comment @-mention write payload (✅ feasible)

ClickUp accepts a **structured `comment` blocks array** on `POST /v2/task/{task_id}/comment`
(and, identically, on `POST /v2/comment/{comment_id}/reply`). ClickUp's own documented example:

```json
{
  "comment": [
    { "text": "I need someone to look at this comment. Maybe " },
    { "type": "tag", "user": { "id": 1234567 } },
    { "text": " if you have time to check this out. Thanks!" }
  ]
}
```

Shape rules, **per ClickUp's published example** (not a captured live request — see the
caveat below):

- **Plain run:** `{ "text": "…" }`.
- **@-mention run:** `{ "type": "tag", "user": { "id": <numericUserId> } }`. The tag block
  appears to need **only** `type` + `user.id`; the example carries no `text` and no
  `attributes` on the tag block. ClickUp fills the rendered `@Name` text server-side and, on
  read-back, stores the block as `{ text:"@Name", type:"tag", user:{ id } }` — exactly what
  our read-side `CommentBlock` / `MapMentionedUserIds` (#167) already consumes.
- **Multiple tags per request** are allowed; text can precede and follow each tag.
- **`comment_text` is not required alongside the blocks.** ClickUp's example sends `comment`
  only. The behavior when *both* are sent is unverified (it may duplicate the body), so
  **send only one**. **Recommendation for H:** send the `comment` blocks array *by itself*
  when there is at least one mention, and leave the existing plain-text-only path
  (`comment_text`) untouched for mention-free comments.

> **⚠️ Confidence caveat (the `attributes` question #321 flagged).** `developer.clickup.com`
> is egress-blocked this session, so the tag-block shape above comes from ClickUp's *published
> example* + our read-side model, **not** from a captured live write request/response as
> #321's AC ideally wanted. The specific claim "a mention tag needs no `attributes`" is
> therefore documented-not-captured. **The integration test in the test plan below (self-mention
> → re-fetch → assert `MentionedUserIds` contains the id) is the confirmation gate**: H should
> run it once under `CLICKUP_TOKEN` to lock the shape before hard-coding the block builder. If
> a bare `{type:"tag",user:{id}}` is rejected or the mention doesn't materialize, add the
> `attributes` object per the read-side #167 note.
- **`notify_all`** stays a sibling top-level field (unchanged from the plain path).
- **`attributes`** (per-block object) exists for *rich formatting* — `code-block`, `list`
  (`bullet`/`ordered`/`checked`), bold/italic, links — and is **out of scope for H**, which
  only needs mention tags. Model it as an optional/forward-compatible field but don't build
  on it now.

The numeric `user.id` comes from the workspace members endpoint we already map
(`GetWorkspaceMembersAsync` → `WorkspaceMember.Id`), which is exactly the id the #324 mention
picker (J) selects and #325 (K) will hand to the composer.

### Kiota / spec gotcha to expect during regen (H)

Our read `Comment` schema's `comment` blocks property is generated as **`CommentProp`** — Kiota
mangled it because the property name collided with the `Comment` class name (mirrors
`StatusProp`/`PriorityProp`; see the #167 plan). On `CreateCommentRequest` the property `comment`
doesn't collide with its *containing* class name — but a **sibling schema type `Comment` exists
in the same namespace**, and the PascalCased property `Comment` could still be mangled to avoid
colliding with that type (so a `CommentProp`-style rename is plausibly *more* likely than not).
H must therefore **verify the actual generated member name after regen** and bind the facade to
whatever Kiota emits (do not assume it is `Comment`). Reusing
the existing `CommentBlock` component keeps the mapping identical and the generated surface
minimal — Kiota omits unset properties, so a write block built as
`new CommentBlock { Type = "tag", User = new User { Id = 1234567 } }` serializes to exactly
`{ "type":"tag", "user":{ "id":1234567 } }` with no stray `text`/`username`/`email`.

---

## Finding 2 — Description @-mention verdict (⚠️ rescope L)

**A real, linked @-mention inside a task description is not expressible through the documented
v2 API.**

- `PUT /v2/task/{id}` description writes take either `description` (plain text — what
  `SetTaskDescriptionAsync` sends today) or `markdown_content` / `markdown_description`
  (Markdown). There is **no** structured-block description payload analogous to the comment
  `comment` array — a description is a string field, not a blocks array.
- Markdown does not carry ClickUp's structured `tag` blocks, so an `@name` typed into
  `markdown_content` lands as literal text, **not** a linked mention that notifies the user
  or resolves to a member id. (ClickUp's in-app description editor builds real mentions
  through an editor surface the public v2 API doesn't expose.)

**Recommendation for L (#326):** rescope. Two viable slices, in order of preference:

1. **Ship the structured *description write* (markdown) without real mentions.** Move
   `SetTaskDescriptionAsync` onto `markdown_content` so the editor (Ctrl+E, #326's other half)
   can write formatted descriptions, and treat `@name` as plain markdown text only — clearly
   documenting that description mentions don't link/notify via the API. This delivers the
   structured-write half of L and drops the infeasible mention-link half.
2. **Defer description @-mentions entirely** until ClickUp exposes a structured description
   write (or a v3 capability — see #2), tracking it as a known API limitation.

**Exact spec delta for L option 1** (the markdown structured-write, no mention links). Add a
`markdown_content` field alongside the existing plain `description` on `UpdateTaskRequest`:

```jsonc
// components.schemas.UpdateTaskRequest.properties  (L option 1)
"markdown_content": {
  "type": "string",
  "description": "Task description as Markdown (ClickUp renders it). Use INSTEAD of `description` for a formatted write; ClickUp reads back the rendered form via text_content/markdown_description. Carries plain @name text only — not a linked mention (see Finding 2)."
}
```

`SetTaskDescriptionAsync` then sends `markdown_content` instead of `description` (guard: still
reject `null`; empty string clears). No mention plumbing. **Note this delta is provisional on
the maintainer accepting the L rescope** — if L stays "real description mentions", it is
blocked at the API and the delta is moot; the concrete change lands in L's own PR once the
rescope is confirmed. That keeps #321's "exact delta for L" clause consciously answered
(feasible slice specified) rather than left implicit.

**One live check worth doing before L lands** (H/L, gated on `CLICKUP_TOKEN`): `PUT` a
description containing `@Name` via `markdown_content` and read the task back to confirm it
does *not* materialize as a linked mention. Expected: literal text. This removes the last
sliver of doubt without shipping probe code.

---

## Finding 3 — @Brain / Super-Agent tagging verdict (❌ no-go)

**@Brain and named Super Agents are not addressable as mention targets via the v2 API.**

Empirical evidence from this workspace's live `GET /team` members list (via the ClickUp MCP):

- **535 members, all human.** Scanning every member's `name` / `username` / `email` for
  `brain`, `bot`, `agent`, `ai`, `assistant`, `autopilot`, `super` returned **zero** AI/agent
  entries — the only `@clickup.com` addresses are two human ClickUp staff accounts.
- A direct `find_member_by_name("Brain")` returned **null**.

A mention block requires a numeric `user.id` that comes from the members endpoint; since
@Brain / Super Agents have **no member id there**, there is nothing to put in
`{ "type":"tag", "user":{ "id": … } }`. ClickUp Brain is invoked through its own AI feature
surface (in-app / separate AI endpoints), **not** by tagging a "user" in a comment, so the
public v2 comment API cannot trigger it as a mention.

**Verdict:** exclude @Brain / Super-Agent tagging from H and the #324/#325 mention picker
scope. The picker should surface **human workspace members only**. If ClickUp later exposes AI
agents as mentionable identities (or via a dedicated endpoint), reopen this as a new issue.

> **Caveat on the evidence.** This is an **empirical result for the current workspace**, not an
> exhaustive check of every ClickUp AI/agent API. Two limits: (a) "535 members, all human" is a
> keyword scan (`brain`/`bot`/`agent`/`ai`/`assistant`/`super`) — an agent member named
> off-pattern would be missed; (b) whether ClickUp Brain / Super Agents are even *provisioned*
> in this workspace was not independently confirmed, so absence-from-`GET /team` can't fully
> distinguish "the v2 comment API can't express agent mentions" from "no agent identities exist
> here to tag." The practical conclusion for H/the picker (surface human members only) holds
> regardless; treat the categorical "v2 can't do it" as strongly indicated rather than proven.

---

## The exact `clickup-openapi.json` deltas for H (#322)

Applied to `src/ClickUpTodo/ClickUp/clickup-openapi.json`. **Never hand-edit `Generated/`** —
edit the curated spec, then regen.

### 1. Reuse the existing `CommentBlock` component for the write side

`CommentBlock` already exists (`{ text?, type?, user? }`). It is sufficient for mention tags.
Optionally future-proof it for rich formatting by adding a free-form `attributes` object —
**only if** you want H's block builder to be forward-compatible; it is **not** needed for
mentions and can be deferred:

```jsonc
// components.schemas.CommentBlock.properties  (OPTIONAL, forward-compat only)
"attributes": {
  "type": "object",
  "additionalProperties": true,
  "nullable": true,
  "description": "Per-run rich-text styling (code-block, list bullet/ordered/checked, bold/italic, link). Not required for @-mention tags; modelled for forward-compat."
}
```

Kiota emits an untyped/additional-data bag for a free-form object; keep it out unless a later
rich-formatting issue needs it, to keep the generated surface minimal.

### 2. Add the structured blocks array to `CreateCommentRequest`

```jsonc
// components.schemas.CreateCommentRequest
{
  "type": "object",
  "description": "Comment body for POST /task/{task_id}/comment and POST /comment/{comment_id}/reply. Send `comment_text` for a plain comment, OR the structured `comment` blocks array to include @-mention tag blocks (do not send both). `comment_text` is no longer strictly required once blocks are supported.",
  "properties": {
    "comment_text": { "type": "string", "nullable": true },
    "comment": {
      "type": "array",
      "nullable": true,
      "items": { "$ref": "#/components/schemas/CommentBlock" },
      "description": "Structured rich-text runs. A plain run is { text }; an @-mention run is { type:\"tag\", user:{ id } }."
    },
    "notify_all": { "type": "boolean", "nullable": true }
  }
}
```

Note the change to `required`: today `comment_text` is `required`. With the blocks path added,
**drop `comment_text` from `required`** (either the text or the blocks array satisfies the
request). The facade guards "at least one of text/blocks non-empty" at the boundary instead.

No path/operation changes are needed — `POST /v2/task/{task_id}/comment` and
`POST /v2/comment/{comment_id}/reply` already reference `CreateCommentRequest`, so both the
task-comment and reply write paths gain structured blocks for free (relevant to #330's reply
composer too).

### 3. Regen

```bash
dotnet tool restore
pwsh scripts/regen-client.ps1     # or run the underlying `dotnet kiota generate …` if pwsh is absent
```

Expect: a regenerated `CreateCommentRequest.cs` gaining a blocks-array property (verify its
Kiota member name — see the gotcha in Finding 1); `CommentBlock.cs` unchanged unless you added
`attributes`. Confirm no unexpected churn under `Generated/`; commit only regen output.

---

## Facade + test plan H should follow

**Facade** (`ClickUpClient` + `IClickUpClient`), additive — keep the existing plain-text
overloads exactly as-is:

- A block-builder or overload, e.g.
  `Task<CommentItem> CreateTaskCommentAsync(string taskId, IReadOnlyList<CommentBlock> blocks, CancellationToken ct = default)`
  — or a small domain block type (e.g. `CommentRun` = plain text | mention(userId)) mapped to
  generated `CommentBlock` inside the facade so the app never sees generated types (per the
  README's "rest of the app must not see generated types" rule). A domain `CommentRun` is the
  cleaner choice: the #325 composer builds `[Run("hi "), Mention(183), Run(" pls")]` without
  touching Kiota models.
- Posts `{ "comment": [...] }` (no `comment_text`) with `notify_all=false`; reuses the same
  minimal-response mapping as `CreateTaskCommentAsync` (build the returned `CommentItem` from
  the response `id`/`date` plus a flattened preview of the text runs, since the create response
  echoes nothing — same contract documented on the existing method).
- Records the same `CommentFields` change-marker nudge (#294) as the plain path.
- Guard "at least one non-empty run" at the boundary (mirrors the existing
  `ArgumentException.ThrowIfNullOrWhiteSpace(text)`), because dropping `comment_text` from
  `required` means the API would otherwise accept an empty body.

**Tests** (mirror `ClickUpClientCommentCreateTests`, the capturing-`HttpMessageHandler`
pattern — offline, no token):

- Posting `[Run("hi "), Mention(183)]` sends body `{ "comment":[{"text":"hi "},{"type":"tag","user":{"id":183}}], "notify_all":false }`,
  method POST, URI `/v2/task/{id}/comment`; **no** `comment_text` key present.
- A single mention with no surrounding text serializes as one `{type:"tag",user:{id}}` block
  (no stray `text`).
- The returned `CommentItem` carries the response `id`/`date` and a text preview; `TaskId`
  stamped; empty/whitespace-only run list throws at the boundary without a network call.
- **Fake backend / fixtures** (`tests/…` E2E harness): teach the fake `POST comment` handler
  to accept and echo the structured `comment` array so the #325 composer's `tui-validate`
  scenario (which lands with K) can exercise a mention-bearing post.
- **Integration** (`SkippableFact`, `CLICKUP_TOKEN` + a task id): post a comment mentioning
  the authenticated user (`GetMeAsync().Id`) and re-fetch to confirm `MentionedUserIds`
  contains that id — a self-mention keeps the probe from notifying colleagues. Self-skips
  without credentials.

---

## Summary for the epic

| Question | Verdict | Consumed by |
| --- | --- | --- |
| Comment @-mention write payload | ✅ `comment` blocks array; tag = `{type:"tag",user:{id}}`; no `comment_text` needed | **H #322**, and #330 reply composer |
| Description @-mention | ⚠️ Not a real linked mention via v2; markdown text only → **rescope L** | **L #326** |
| Structured description *write* | Possible via `markdown_content` (no mentions) | **L #326** (option 1) |
| @Brain / Super-Agent tagging | ❌ No member id in `GET /team`; not mentionable via v2 comment API | Scopes the #324/#325 picker to humans |

## Invariants respected

- **No `Generated/` hand-edits** — H edits the curated spec + regen only.
- **Auth quirk untouched** — raw `Authorization` header, no `Bearer`.
- **No TUI change in this spike** — findings only; the composer wiring is #325 (K).
- **Live probes are read-only** — the one recommended write test is `SkippableFact`, gated,
  and self-mentions to avoid notifying others.
