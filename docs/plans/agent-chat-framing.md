# Agent chat (A, #496): framing decision + UX design

Part of the Super-Agent-chat epic **#491**. **Blocks C (#498), D (#499), E (#500) and F (#501).**
Design work — **no shipping code** in this slice. The deliverable is this document:
a recorded decision with rationale, the `--chat` token grammar and disambiguation
rule, wireframes + a keymap for the conversation surface, and an explicit statement
of which sub-issues change shape.

> **Status: decision recorded for maintainer ratification.** The issue body carries
> an explicit **owner steer** toward option 3 (a `--chat` app host) and delegates the
> remaining open questions to "this issue should settle …" rather than asking the
> maintainer to choose. This doc formalizes that steer and settles the delegated
> design questions; merging the PR is the ratification. Where a question is *not*
> settled here, it says so.

---

## The decision, in one line

Build the Super-Agent chat experience as **option 3 — its own application host,
`clickup-todo --chat`** — a fourth root host in the exact shape of `SingleTaskApp`
(`--task`) and `FeedApp` (`--feed`), which Dispatch *launches into a new terminal
tab* rather than hosting inline. The provider *selector* still lives in the Dispatch
pane (option 1 is not mutually exclusive on the selector); only the **conversation**
lives in the standalone host.

---

## The three options (verbatim from #496) and why 3 wins

| # | Option | Cost | Verdict |
| - | ------ | ---- | ------- |
| 1 | Inside the Dispatch pane — a provider row; conversation renders in an `AgentRunScreen`-like screen | Cheapest | **Rejected** for the *conversation* (kept for the *selector*) |
| 2 | A root-level Super Agents screen in the dashboard host — own F-key, own thread list | Middle | **Rejected** |
| 3 | Its own application host (`clickup-todo --chat`) that Dispatch launches into | "Cheaper than it sounds" | **Chosen** ⭐ |

### Why option 1 is rejected for the conversation

Option 1 hangs a **long-lived, non-task-scoped** surface (an ongoing conversation
that *outlives the task you started it from*) off a **one-shot, task-scoped** one
(the Ctrl+A Dispatch pane, which today gathers prompt + session-mode + working-dir +
post-to-comments + launch-location and fires a single terminal launch —
`DispatchPaneModel` / `DispatchRequest.cs:32-37`, which carries **no** provider
field). The lifecycle mismatch is the whole point of #496: an agent conversation is
not a dispatch. Keeping the conversation in the pane would force the pane to grow a
second, conflicting lifetime model.

The *provider list*, by contrast, genuinely does belong near Dispatch — see
"the selector still lives in Dispatch" below. Options 1 and 3 are **not mutually
exclusive on the selector**; what differs is where the *conversation* lives.

### Why option 2 is rejected

A root-level Super-Agents screen spends finite **F-key / screen budget** in the
dashboard host on a surface that isn't task-triage — the dashboard's job. It also
couples the conversation's lifetime to the dashboard process (quit the dashboard,
lose the surface) for no benefit over a standalone host that can sit beside the work
in its own tab.

### Why option 3 is *cheaper* than it sounds — the repo already has the pattern

The single strongest argument, and it is grounded in code:

- **A second root host already exists and is proven.** `SingleTaskApp`
  (`Tui/SingleTaskApp.cs`, `public sealed class`) is selected by `--task` in
  `Program.cs` and is *deliberately not* the dashboard's `TodoApp` in a "no-list"
  mode — "so it has zero blast radius on the dashboard" (class doc,
  `SingleTaskApp.cs:22-41`). It shares the service graph (`TaskService`, `AppConfig`,
  `ConfigStore`, `IBrowserLauncher`, the #377 change-marker nudge channel,
  `AssigneeFrequencyCache`) via its constructor (`SingleTaskApp.cs:108-129`) and owns
  its own `Run(driverName)` lifecycle: diffing ANSI backend → `Application.Init` →
  `Build` → marker poll → `Application.Run(_window)` → `TuiTeardown`/`Shutdown`
  (`:151-181`), with its own `List<Screen> _stack` + `ShowScreen`/`CloseScreen`
  (`:614-665`).
- **A *third* read-mostly host already exists too.** `FeedApp` (`Tui/FeedApp.cs`,
  `--feed`) is, per its own doc (`:19-24`), "a third root host in the shape of
  `SingleTaskApp`" — seed from a cached snapshot, kick a live refresh on show, own an
  auto-refresh timer + 4s marker poll, stack Help/exit over a single root screen. A
  chat host is **modeled on `FeedApp` most directly** (read-mostly, live-refreshing,
  single root screen).
- **The relaunch-into-a-new-tab machinery is already shared and generic.** Ctrl+Enter
  "open task in a new terminal tab" and the whole agent-dispatch launch both run
  through `AppLaunchCommand` (`Agent/AppLaunchCommand.cs`; `ForTask` builds argv
  `[--task, id]` and resolves the executable three ways — `clickup-todo` on PATH →
  real apphost → best-effort), `TerminalCommandPlanner.PlanAppLaunch`
  (`:56-72`, the cross-platform emulator matrix, same one dispatch uses), and the
  UI-agnostic `AppTabLaunch` glue (`Agent/AppTabLaunch.cs`: `Options` / `Opening` /
  `Opened` / `Fallback`). `SingleTaskApp.LaunchAppTabForTask` (`:716-750`) and
  `FeedApp.LaunchAppTabForTask` (`:405-439`) are structurally identical bar the
  flash label — the shared machinery (`AppLaunchCommand.ForTask`, the `AppTabLaunch`
  helpers, the re-entrancy guard, the off-thread launch) is the same in both.

So a `--chat` host reuses patterns the repo has already proven: **two** standalone
root hosts (`--task`, `--feed`) as the hosting shape, plus the shared **tab-relaunch**
machinery as the launch mechanism. Option 3's minimum shape is just
`--chat <token>` + the conversation surface (E/#500); it is additive and has zero
blast radius on the dashboard.

### Frame the host as `--chat`, not `--agents` (owner steer, adopted)

The host is a **chat mode**, not an agent mode. The API grain is
channels-and-messages; Super Agents are simply *participants*. Adopting the owner
steer:

- **Bare `--chat`** → a **conversation browser** (pick an existing channel/DM, or
  start a new one). *Deferred indefinitely — see #501; direct-open is resume for the
  common case.*
- **`--chat <token>`** → open straight into a conversation: a **channel id / URL**, or
  a **quoted participant name** (`--chat "Recap Rio"`).

This mirrors `--task`'s one polymorphic token (plain id / custom id / URL, classified
by the shared `QuickOpenParser`) exactly, and **must reuse that structure** rather
than invent a second convention (see the grammar below).

**Why the framing is *less* risky, not more:**

- **It weakens the hardest dependency in the programme (#494, the agent directory).**
  Reaching an agent through an existing DM/channel does *not* require resolving its
  name to a negative agent id. The channel-id / URL / Dispatch-supplied forms never
  touch #494 at all; only the *quoted-name convenience form* does. If discovery is
  unreliable, **chat still works** — #494 becomes an accelerator for one input form,
  not a prerequisite.
- **Browse-and-resume stops being bolted on.** The browser *is* the landing screen of
  bare `--chat`, so #501's thread list is native to the host, not a late addition.
- **It matches where the ids come from.** #492's top-ranked discovery pathway is
  channel members — the same surface a chat browser already reads.

**The trade-off accepted deliberately:** our org doesn't use ClickUp Chat outside of
talking to Super Agents, so a general chat client is more surface than we need. The
framing is chosen for the **API grain and the launch ergonomics**, not because we
want a chat product — and the scope boundary below says so explicitly (and the help
text must too).

---

## Settled here #1 — the `--chat` token grammar

Mirror the `--task` structure exactly, so there is one convention, not two:

- A thin arg struct **`ChatLaunchArg`** owning only present/value and the
  `--chat <token>` / `--chat=<token>` / bare-`--chat` shapes — copying
  `TaskLaunchArg.cs` (which also refuses to swallow a following `--flag` as its value)
  for the token form and `FeedLaunchArg.cs` (a valueless switch) for the bare form.
- A pure classifier **`ChatOpenParser.Parse(token) → ChatOpenRef`**, modeled
  one-to-one on `QuickOpenParser.Parse` (`Services/QuickOpenParser.cs:60-83`), which
  classifies **syntactically only** and does *no* network resolution (exactly as
  `QuickOpenParser` never hits the API to classify):

```csharp
public enum ChatOpenKind
{
    ChannelId,   // a bare channel-id token
    ChannelUrl,  // a clickup.com chat/channel URL
    Name,        // a quoted participant/channel name — needs resolution (the only form that can touch #494)
    Invalid,
}

public readonly record struct ChatOpenRef(ChatOpenKind Kind, string Value, string? TeamId = null);
```

Classification rules, in order (mirroring `QuickOpenParser`):

1. **A URL** whose host is a ClickUp host → `ChannelUrl` (parse the channel id out of
   the path, carrying any team id — the analogue of `FromTaskPath`,
   `QuickOpenParser.cs:120-135`). Unambiguous.
2. **A bare token that matches the channel-id shape** → `ChannelId`. (The exact
   channel-id regex is an implementation detail for C/D to pin against a real
   response; the *grammar* — "a bare id is a channel id" — is fixed here. Channel-first
   because the entire host is channels-and-messages and a DM is just a channel.)
3. **Anything else (a quoted free-text token)** → `Name`, resolved at open time
   (below).

> **Note:** the exact syntactic boundary between a channel-id token and a free-text
> name (step 2 vs 3) is the one thing this grammar leaves to C/D to pin against a real
> ClickUp Chat response, because it depends on the real id shape. The **precedence
> semantics** below — which is what C/D said they "genuinely can't proceed without" —
> are fully settled here regardless of that boundary.

---

## Settled here #2 — token disambiguation precedence + "did you mean"

This is the piece #496 flags as the one C/D genuinely cannot proceed without.
Resolution precedence, at open time:

1. **The Dispatch-supplied path is unambiguous and is the common case.** When the user
   picks a Super Agent from the Dispatch selector, the selector already holds the
   resolved entity (kind + id), so it *supplies the token* — the user never types one,
   and there is **nothing to disambiguate**. This path never classifies and never
   touches #494.
2. **A URL / channel-id token → open that channel directly.** No resolution needed.
3. **A bare id that 404s as a channel → retry as a user id → that user's DM channel.**
   (Channel-first, DM-fallback: a DM is a channel, so the message-list surface is
   identical either way.)
4. **A quoted name → resolve against the union of channel names and participant
   (human + Super-Agent) display names**, with this precedence:
   - an **exact channel-name** match wins;
   - else an **exact participant-name** match → that participant's DM;
   - **ties, or ≥2 partial matches → open the conversation browser pre-filtered to the
     token** ("did you mean"), rather than guessing. The browser is the natural
     disambiguation surface, which is a further reason the browser (#501) is *native
     to the host* even though its bare-`--chat` entry point is deferred.

The launch never silently opens the *wrong* conversation: the only non-interactive
resolutions are the exact-match and Dispatch-supplied cases; every ambiguity routes to
an explicit picker.

---

## Settled here #3 — one shared name resolver, not two

Name resolution unions two sources — humans via `GET /team` (#323) and Super Agents
via the directory (#494) — which is the **same union the #495 mention picker already
performs**. Decision: **one shared resolver.** Extract the mention picker's
union + ranking (channel-members-first per #492) into a single provider that both the
`@`-mention picker and chat name-resolution consume, keyed on a shared
`ChatTarget`/`MentionTarget` type. **Do not fork it** — two resolvers guarantee two
ranking behaviours and two directory-staleness stories that drift. Because only the
*quoted-name* form (`ChatOpenKind.Name`) uses it, the shared resolver is off the
critical path for the channel-id / URL / Dispatch-supplied forms.

---

## Settled here #4 — the explicit not-building list (scope boundary)

Build the **primitives** only: channel list, message list, post a message, threaded
replies, cancel an in-flight turn. Do **not** build the social layer — **no**
reactions, typing indicators, presence, attachments, rich composer, unread badges, or
notification handling — unless a later issue argues for one on its own merits. The
help text for `--chat` must say so, so it doesn't imply parity with the PWA. Chat
framing is chosen for the API grain and launch ergonomics, not to build a chat
product.

---

## Settled here #5 — conversation lifetime (feeds F/#501)

Conversations are **server-side and long-lived**; the host holds **no** local
conversation state. Leaving the surface (Esc / quit) just closes the view — nothing is
lost, because the conversation lives in ClickUp. Consequences:

- **Direct-open *is* resume for the common case.** A DM channel is long-lived, so
  `--chat "Recap Rio"` returns to the same ongoing conversation every time — the
  common resume case is covered with no browser existing at all.
- The **conversation browser (#501)** is therefore a *discovery* affordance (for a
  user with many distinct threads who doesn't know which they want), not a completeness
  or persistence requirement. It stays a **stretch goal, deferrable indefinitely**,
  under every option.
- **No local transcript or draft is persisted in v1.** If draft-preservation across a
  close is ever wanted, that is a separate later issue, not part of E.

---

## Wireframes + keymap

The host owns its own screens and — like `SingleTaskApp` / `FeedApp` — handles keys
**directly in its screens** rather than through the platform-agnostic `Keybindings`
table (`Tui/Screens/Keybindings.cs`), which today has "no launch-mode dimension yet."
The keymap below deliberately reuses the app-wide muscle memory that table encodes, so
nothing surprises: `Ctrl+N` compose, `Ctrl+T` reply, `F5` refresh, `F1` help, `Esc`
back, and **bare letters left free for the list's type-ahead (#12)**.

### Conversation view — `--chat <token>` (the minimum shippable shape, E/#500)

```
┌ clickup-todo — chat: Recap Rio (Super Agent · DM) ───────────────────────────┐
│ ▸ Recap Rio           09:41   Morning! Here's the rollup from yesterday's …   │
│   you                 09:42   can you pull the three at-risk items?            │
│ ▾ Recap Rio           09:42   On it. Three items flagged at-risk:             │
│       · #418 stalled 4d   · #402 blocked on a decision   · #517 no owner       │
│     └ 2 replies                                                                │
│ ▸ you                 09:44   thanks — reply in-thread on #402                 │
│                                                                                │
│ … (a turn is in flight)                    Recap Rio is responding…  ⢿         │
├────────────────────────────────────────────────────────────────────────────┤
│ › _                                                                            │
├────────────────────────────────────────────────────────────────────────────┤
│ ✏ Ctrl+N compose  ↩ Ctrl+T reply  ↻ F5  🌐 Ctrl+B  ⤺ Esc  ℹ F1                 │
└────────────────────────────────────────────────────────────────────────────┘
```

- `↑`/`↓`, `PgUp`/`PgDn` — scroll the message list (single sectioned list; #3 model).
- `Enter` on a message — expand / collapse its threaded replies (the `▸`/`▾` toggle),
  reusing the nested-reply rendering the Task Detail Comments/Stream tabs already do.
- `Ctrl+N` — compose a new message to this channel, reusing the **#473 `@`-mention
  authoring composer** (already built for the Ctrl+N comment composer) so mentions work
  the same everywhere.
- `Ctrl+T` — reply in-thread to the selected message (mirrors Task Detail `Ctrl+T`
  ReplyToComment).
- **Cancel an in-flight turn:** while a send/turn is in flight, `Esc` **cancels the
  in-flight turn** and stays on the surface; with nothing in flight, `Esc` **goes
  back** (to the browser if launched from it, else the guarded-exit path). This keeps
  the app-wide "Esc means go back" model, with the in-flight cancel as the one
  contextual override — and is called out so E doesn't invent a second cancel gesture.
- `F5` — refresh the conversation. `Ctrl+B` — open the channel in the ClickUp web app
  (consistent with `Ctrl+B` elsewhere). `F1` — help.

### Conversation browser — bare `--chat` (F/#501, deferred but designed)

```
┌ clickup-todo — chat ─────────────────────────────────────────────────────────┐
│ Conversations                                                    filter: rec_  │
│  ▸ Recap Rio          Super Agent · DM   09:42  Three items flagged at-risk…    │
│    Release Bot        Super Agent · DM   Tue    Deploy 0.1.0-beta.2 succeeded    │
│    #eng-standup       Channel · 6        Mon    (you) posting the rollup now     │
│                                                                                │
├────────────────────────────────────────────────────────────────────────────┤
│ ↩ Enter open   ➕ Ctrl+N new   ↻ F5   ℹ F1   ⤺ Esc quit                          │
└────────────────────────────────────────────────────────────────────────────┘
```

- `↑`/`↓` move; **`type` = type-ahead filter** (bare letters, #12).
- `Enter` opens the selected conversation (into the view above).
- `Ctrl+N` starts a new conversation (pick a participant via the shared resolver, #3).
- `F5` refresh; `F1` help; **`Esc` quits — guarded**, exactly like a `--task` launch
  task or the dashboard (there is nothing to go back to from a launch host).

### How the user gets back to a task

Under option 3 the conversation lives in **its own terminal tab** (Dispatch launches
it via `AppLaunchCommand` + `PlanAppLaunch` + `AppTabLaunch`, the same relaunch as
Ctrl+Enter's task tab). "Back to the task" is therefore the terminal's own tab switch
back to the originating tab — the chat host doesn't stack over the task and never
occludes it. A standalone `--chat` launch (no originating task) simply quits on `Esc`
(guarded), like `--feed` / a `--task` launch task.

---

## Which sub-issues change shape under option 3

| Issue | Slice | Shape under option 3 |
| ----- | ----- | -------------------- |
| **#497** | B — provider model | **Already shipped** (PR #546). Unaffected, but its `DispatchProviderKind` enum (`Configuration/DispatchProvider.cs:8-12`, "room for a future non-local kind … per #491") is the anticipated hook for a `SuperAgent` kind. |
| **#498** | C — provider selector in the Dispatch pane | **Stays.** Adds a Super-Agent provider entry; picking it *launches the `--chat` host in a new tab* (via the relaunch machinery) rather than adding a provider field to `DispatchRequest`. The selector supplies the resolved token, so the primary path needs no disambiguation. |
| **#499** | D — route an agent provider to a conversation (DispatchCoordinator's "third flow") | **Reshaped.** The "third flow" becomes a branch on `provider.Kind == SuperAgent` in the dispatch path (`SingleTaskApp.DispatchAgent` `:543-582` / `DispatchCoordinator`): instead of `ToLauncherOptions()` → terminal CLI, call `AppLaunchCommand`/`PlanAppLaunch` → chat-host tab. **No longer hard-depends on #494** — the DM/channel id carries the routing. |
| **#500** | E — the conversation surface | **The host's core**, and the minimum shippable shape of option 3 (`--chat <token>` + E). Cheaper than #496 originally assumed. **Sequenced behind #493** (the ClickUp Chat v3 client plumbing — see below). |
| **#501** | F — browse & resume (bare `--chat`) | **Stays a stretch,** now *native* to the host as its landing screen, but deferrable indefinitely (direct-open-is-resume covers the common case). |
| **#491** | Epic | Host is `--chat`, a sibling of `--task` / `--feed`, reusing the `SingleTaskApp` / `FeedApp` shape + `AppTabLaunch` relaunch. |

---

## Prerequisite this framing surfaces (already tracked by #493)

**The repo's own ClickUp client has no chat plumbing.** Verified: `Models.cs`,
the curated spec `ClickUp/clickup-openapi.json` (v2-only), and the Kiota-generated
`ClickUp/Generated/V2/` tree have **zero** channel/message/chat types — the generated
request-builder tree covers only Team, Space, Folder, List, Task, Comment, Checklist,
User. ClickUp Chat exists in the repo **only** at the MCP-tool layer, not in the app's
client. So **E (#500) has a hard foundation dependency**: adding the ClickUp Chat
**v3** endpoints (channel list, channel messages, threaded replies, send message) to
the **curated spec** `clickup-openapi.json`, regenerating the client
(`pwsh scripts/regen-client.ps1` — never hand-editing `Generated/`), and surfacing
DTOs through `Models.cs` + the `ClickUpClient` / `IClickUpClient` facade as stable
domain records.

**This is already tracked by [#493](https://github.com/rbcministries/clickup-todo-cli/issues/493)**
("Super Agents (B): Chat / v3 API client strategy + plumbing", under the Super-Agents
epic #490) — no new issue is needed. #493 even records the same verified state
("`grep -ril chat` returns zero matches", the v2-only spec) and offers the route
decision (extend the curated spec vs a separate v3 client vs a hand-rolled helper).
E (#500) should be sequenced **behind #493**; this framing simply confirms that
sequencing and that the domain-facade boundary (`ClickUpClient`, never generated
types) is preserved.

---

## What this decision does *not* settle (explicitly)

- The exact ClickUp Chat v3 endpoint shapes / DTOs and the channel-id regex — pinned by
  the foundation issue above against real responses, then consumed by E.
- Whether cancellation of an in-flight agent turn is server-supported or client-only
  (abandon the read) — measured in E against the real Chat API; the *gesture* (`Esc`)
  is fixed here, the *mechanism* is not.
- The `windows` / `dotnet` driver behaviour of the new host (the `tui-validate` harness
  is ANSI-only per CLAUDE.md) — validated when E ships, same caveat every host carries.
- Anything on the not-building list (§4) — each would need its own issue arguing its
  merit.

## Hard rules (this slice)

- **No shipping code, no `Generated/` edits, no spec change, no Kiota regen** — this
  slice is documentation only.
- The design keeps the **single sectioned `ListView`** model (#3) and **bare letters
  reserved for type-ahead** (#12); the host adds no second focusable pane and no
  bare-letter binding.
- All command gestures are chords / function keys, consistent with the app-wide
  `Keybindings` conventions even though the host handles keys in-screen.
