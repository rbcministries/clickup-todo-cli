# Spike (A): Super Agents — negative-id write probe, discovery source & Chat API surface

Issue: **#492** (part of epic **#490** — Super Agents discovery/directory/@-mention). Gates every
other sub-issue in #490 and, bar its **B** (#493), all of #491 (Super-Agent chat). **Investigation
spike, no shipping code.** Its job: settle, *before* building, whether Super Agents are reachable the
way #321's revised Finding 3 suggested, through which endpoints, and at what cost — then hand #490 **B**
(#493) the concrete endpoint list + spec/client strategy it will execute, and give a go/no-go per
downstream thread.

## Method & evidence (and its limits)

The authoritative pages under `developer.clickup.com` remain **egress-blocked** for this session (HTTP
403 at the proxy — the same block #321 hit), so the API-shape findings are triangulated from **web-search
snippets** of those pages plus **live read-only calls** against the maintainer's workspace via the ClickUp
MCP. This was an **unattended scheduled run**, so the two questions that require a live, agent-triggering
**write** (Q1's trigger step, Q5's latency) were **not executed** — they have an outward-facing side effect
(notifying real people / spending paid AI credits) on a production ministry workspace and are split into
**#577** for a supervised run. Everything verifiable read-only is settled below; each claim is tagged
**[live]** (observed here), **[doc]** (ClickUp docs via search snippet), **[repo]** (this codebase), or
**[deferred]** (needs #577).

**Confidence.** The enumeration verdict (Q2) and the negative-id-agents-exist observation are **high
confidence** — both are direct live reads. The Chat v3 endpoint list (Q4) is **high confidence on paths,
medium on exact request/response schemas** (documented-not-captured — the reference pages are blocked).
The mention-vs-DM trigger analysis (Q1) is **high confidence as an API-capability boundary** but the actual
negative-id trigger is **unverified** (the whole point of #577).

---

## Finding 0 — Super Agents exist here and are first-class chat actors ✅ [live]

`get_chat_channels` on this workspace returns channels and DMs whose **`creator` is a negative id**, e.g.
`-10466700` (the "Recap Rio" id named verbatim in #492), `-16203595`, plus the system id `-1`. So the
#321-revised observation holds **now**: negative-id agent identities are real and actively create/own chat
objects. Treating **"negative id ⇒ agent" as a heuristic, not a contract** (the sign convention is
undocumented) — but it is a reliable-looking one here.

## Finding 1 — The trigger surface: task-comment tag vs. chat mention vs. chat DM ⚠️ [deferred]

Three candidate ways to make an agent respond, and they are **not** equivalent:

| Path | Mechanism | Verdict |
| --- | --- | --- |
| **Task comment tag** (v2) | `POST /v2/task/{id}/comment` with a `{ "type":"tag", "user":{ "id": N } }` block | Task comments support a **real, notifying** tag (#321 Finding 1). **Open question:** does a *negative* `id` resolve/notify/trigger the agent? → **#577 Probe A.** *This is #492's literal Q1 and the decider for #490 D.* |
| **Chat channel @-mention** (v3) | `@Name` text in a `POST …/chat/channels/{id}/messages` body | **Likely NO-GO via public API.** [doc] The v3 chat message API has **no true @-mention** — markdown `@Name` is *visual-only* and doesn't register/notify; there's an open feature request "Support True @Mentions in Chat Message API (Tagged Users + Notifications)". A visual-only mention won't fire a mention trigger. |
| **Chat DM to the agent** (v3) | Post any message into the DM channel with the agent | **Likely GO** [doc] — product docs: "send a direct message to the Super Agent to trigger it." The trigger is *message-posted-in-my-DM*, **not** a mention token, so the chat mention limitation above doesn't block it. → **#577 Probe B.** |

**Consequence for the epic:** the two live epics take **different** trigger paths. #490 **D**
(agent @-mention *in a task comment*) rides the v2 task-comment tag and is genuinely open pending
**#577 Probe A** — it is *not* foreclosed by the chat-mention limitation, because task comments are a
different, tag-capable surface. #491 **D/E** (an in-app *conversation*) should ride the **DM path**, not a
channel @-mention.

> **Why the negative-id task-comment probe is worth running even though chat mentions are blocked:** the
> task-comment tag block takes a bare numeric `user.id` and ClickUp resolves the display name/notification
> server-side. Nothing in the *documented* shape forbids a negative id; whether the server *accepts and
> routes* one to an agent is purely empirical. If it works, #490 D is a small delta on the existing
> comment-block write (already spec'd by #321/#322). If it 4xxs or silently no-ops, D collapses onto the
> DM path (or is dropped).

## Finding 2 — Enumeration / discovery source ✅ [live] (members endpoint ruled out)

#492 ranked four candidate sources. Verdicts:

1. **Workspace members endpoint (`GET /team`)** — ❌ **not a source.** [live] A full read returns **542
   members, every one a positive id — zero negatives.** Fields are `id / name / username / email /
   profilePicture` only. The negative-id agents that demonstrably exist (Finding 0) **do not appear here.**
   This confirms #321 Finding 3's observation persists and *disqualifies* the members endpoint for agent
   discovery. (It also means `find_member_by_name` / `resolve_assignees`, which ride this endpoint, cannot
   see agents.)
2. **`getChatChannelMembers` / `getChatChannelFollowers` (v3)** — 🟡 **most promising; verify.** [doc]
   Both are documented (`GET …/chat/channels/{channel_id}/members` and `…/followers`). Agents are known to
   be channel-associated identities (Finding 0 — they *create* channels), so the members list is the
   natural place they'd surface with a display name + id. **Not callable through the MCP tool surface
   available this run** (no members/followers tool is exposed), and the reference page is egress-blocked, so
   its response shape (does it include agents? with what fields?) is **unverified** — the first thing #493
   should confirm.
3. **Scan chat-channel message authors for negative `user_id`s** — 🟡 works but partial. [live-ish]
   We already see negative-id *creators* on channels, so scanning *authors* would surface agents that have
   posted — but only those, and it's an expensive fan-out. A reasonable *fallback/supplement*, not the
   primary.
4. **Manual config seed** — ✅ safe fallback. A `config.json` list of `{ id, name }` agent entries
   sidesteps discovery entirely; robust when the above are unavailable.

**Recommendation for #494 (AgentDirectoryCache):** primary = `getChatChannelMembers` (once #493 confirms it
returns agents), supplemented by an author-scan of channels the user is in, with a config-seed override.
The members endpoint is **not** wired in for agents.

## Finding 3 — Id stability ⚠️ [deferred / observational]

Whether a negative id survives an agent **edit** (prompt/settings change) vs. only until **recreation** was
**not verifiable read-only** (would need to edit an agent and re-read — a mutation on a production agent).
Observationally the ids look durable (the same `-10466700` matches #492's earlier capture). **Recommendation
for #494:** treat negative ids as *recreation-stable but not guaranteed* — a **moderate TTL + refresh +
evict-on-failure** registry (which #494 already plans) is the safe posture; don't hard-cache an id forever.

> **[repo] constraint the picker (#495) must handle:** the app currently treats non-positive ids as
> "not a real user" — `MentionDetector.MentionSpec.ForUser` sets `UserId` only when `user.Id > 0`, and
> `AssigneeFrequencyCache` skips `entry.Id <= 0`. Agent identities (negative ids) are therefore **invisible
> to today's mention matching and assignee caching.** #495 (`MentionTarget = Human | Agent`) must relax
> these guards for the Agent case rather than inheriting the `> 0` filter.

## Finding 4 — Chat v3 API surface for a conversation ✅ [doc] (paths solid, schemas to confirm)

Base: `https://api.clickup.com/api/v3/workspaces/{workspace_id}/chat/...` (the app already knows the
workspace id; auth is the **same personal token, raw `Authorization` header** — the existing
`ClickUpTokenAuthProvider` covers v3 unchanged). The endpoints a conversation needs:

| Purpose | Method + path | Thread |
| --- | --- | --- |
| List channels (incl. agent DMs) | `GET …/chat/channels` | discovery, #491 F |
| Retrieve a channel | `GET …/chat/channels/{channel_id}` | #491 F |
| **Create a DM channel** (with the agent) | `POST …/chat/channels/direct_message` | #491 D |
| Channel **members** | `GET …/chat/channels/{channel_id}/members` | #494 discovery |
| Channel **followers** | `GET …/chat/channels/{channel_id}/followers` | #494 discovery |
| **Send a message** | `POST …/chat/channels/{channel_id}/messages` | #491 D/E |
| Retrieve messages | `GET …/chat/channels/{channel_id}/messages` | #491 E/F, reply polling |
| Retrieve message replies | `GET …/chat/messages/{message_id}/replies` (threaded) | #491 E |

**Known constraints [doc]:**

- The Chat API is flagged **experimental / subject to change at any time** — a real risk for #493's client
  strategy (below).
- The message `content` param is **capped ~980 characters** — a hard constraint on #491 **E**'s
  conversation surface (long agent turns may need paging or truncation handling).
- **No push / webhooks for chat in-scope here** — replies must be **polled**. Cadence + latency are
  unmeasured (**#577 / Q5**); #491 E's own risk note stands.

Exact request/response JSON schemas are **documented-not-captured** (reference pages egress-blocked); #493
should capture them from a live call (or by observing the MCP server's traffic) before hard-coding shapes.

## Finding 5 — Latency & rate limits ⚠️ [deferred]

No push mechanism ⇒ a **poll loop** is unavoidable for a conversational surface. Actual reply latency and a
tolerable cadence are **unmeasured** (needs a live trigger — **#577**). Rate-limiting: v3 chat is subject to
ClickUp's standard limits; the app already handles 429s centrally (`ClickUpRateLimitHandler`,
`X-RateLimit-Reset` honoring), so the plumbing exists — but a tight poll loop must respect it. **Measure
before committing to the #491 E UX; don't assume.**

---

## Go / no-go per downstream thread

| Thread | Verdict | Basis |
| --- | --- | --- |
| **#490 D** — agent @-mention in a **task comment** | ⚠️ **CONDITIONAL — decided by #577 Probe A.** Genuinely open; *not* foreclosed (task comments support real tags). | Finding 1 |
| **#491 D/E** — in-app agent **conversation** | 🟡 **GO via the DM path** (create DM channel → post → poll), **NO-GO via channel @-mention** (no true API mention). ~980-char cap, poll-only. | Findings 1, 4, 5 |
| **#491 F** — **browse / resume** conversations | ✅ **GO (read-only)** — list channels + get messages/replies are documented reads; agent DM channels are enumerable. Resume = re-open the DM and read history. | Findings 0, 4 |
| **#490 C / #494** — agent **registry** | 🟡 **GO** — source = `getChatChannelMembers` (verify in #493) + author-scan + config-seed; **not** the members endpoint. Moderate TTL. | Findings 2, 3 |

## What #490 B (#493) should execute — client strategy

The curated OpenAPI spec is **v2-only** (0 chat/v3 paths; base server `https://api.clickup.com/api`), and the
`ClickUpClient` facade has **no chat/channel client** [repo]. Two strategies for adding v3 chat:

- **Option 1 — extend the curated spec + regen.** Add the `/v3/workspaces/{workspace_id}/chat/...` paths to
  `clickup-openapi.json` and regen Kiota. *Pros:* one generated client, consistent mapping. *Cons:* v3 is
  **experimental/unstable** (churn re-breaks the generated surface); the curated spec exists precisely to
  tame v2's schemas, and v3's would need the same hand-curation; mixing v2+v3 in one client.
- **Option 2 — a small hand-written v3 chat client (recommended for the spike).** The needed surface is
  tiny (list channels, create/find DM channel, post message, get messages/replies, get members). A thin
  hand-written `ClickUpChatClient` — **separate from `Generated/`, so it doesn't violate the "never
  hand-edit generated code" rule** — reusing the existing auth provider + rate-limit handler, avoids
  regenerating the whole client for an unstable, ~5-endpoint experimental surface, and keeps the v2
  generated client clean. Map its responses into new stable domain records (mirroring the facade pattern).

**Recommendation:** Option 2 while Chat v3 is experimental; revisit Option 1 if/when it stabilizes. Either
way, expose only stable domain types to the rest of the app (README rule).

## Explicit statement of what remains unverified (per #492 AC)

Tracked by **#577** (supervised run):

1. **Q1 trigger** — whether a **negative agent id in a task-comment tag** resolves/notifies/triggers
   (Probe A), and whether a **chat DM** triggers the agent (Probe B). *No live write was performed this run.*
2. **Q2 primary source** — whether `getChatChannelMembers/Followers` actually **returns agents** (endpoint
   not reachable via the MCP surface here; reference page egress-blocked).
3. **Q3** — id stability across an agent **edit** vs. recreation (needs a mutation on a live agent).
4. **Q4 schemas** — exact v3 chat request/response JSON (paths solid; bodies documented-not-captured).
5. **Q5** — reply **latency** and a tolerable **poll cadence** (needs a live trigger).

## Invariants respected

- **No `Generated/` hand-edits, no spec/API change, no Kiota regen** — this spike is docs-only.
- **No live agent-triggering writes** in this unattended run — the outward-facing probes are deferred to a
  supervised session (#577); no scratch task was created, no agent was pinged.
- **No private workspace content committed** — only structural facts (member count, id signs) and the
  negative-id example already public in #492.
- **Auth quirk untouched** — v3 reuses the raw `Authorization` header (no `Bearer`).

## Summary for the epic

| Question | Verdict | Consumed by |
| --- | --- | --- |
| Agents exist / negative-id / chat-active | ✅ confirmed live | #490, #491 framing |
| Negative-id **write** trigger (task-comment tag) | ⚠️ deferred → **#577 Probe A** | **#490 D** |
| Chat **DM** trigger | 🟡 likely-go, deferred → **#577 Probe B** | **#491 D/E** |
| Enumeration source | ✅ members endpoint ruled out; `getChatChannelMembers` = verify; config-seed fallback | **#490 C / #494** |
| Chat v3 API surface | ✅ paths listed; schemas to capture | **#490 B / #493** |
| Client strategy | hand-written v3 chat client (Option 2) recommended | **#493** |
| Id stability / latency / rate limits | ⚠️ deferred → **#577** | **#494, #491 E** |
