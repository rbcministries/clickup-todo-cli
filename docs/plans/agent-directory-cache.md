# AgentDirectoryCache — local agent registry (config-seed + pure-logic foundation)

Issue: **#494** (Super Agents **C**, part of epic **#490**). Gated on **A** (#492, the
discovery/Chat-API spike — **merged**, `docs/plans/super-agents-spike.md`) and **B** (#493, the
v3-chat client strategy — **still open/undecided**).

## What this slice ships

The **decision-free, CI-verifiable foundation** of the agent registry — everything the merged spike
settled as independent of the still-undecided v3 client strategy (#493). It lands foundation-first,
mirroring the other #490/#537 slices that shipped their tested core ahead of a blocked/decision-gated
remainder (#599 comment-delete facade, #602 custom-field projection, #603 subtask-create facade).

- **`AgentDirectoryCache`** — a workspace-keyed registry of `{ id, name, purpose? }` agent entries.
  It substitutes for a **missing enumeration endpoint** (the spike ruled out `GET /team`: agents have
  negative ids and never appear there), so the design risk is **staleness/invalidation**, not compute:
  - a **config seed** (`SuperAgentSettings.Agents`) the user pins by hand — the settled fallback that
    works with discovery unavailable (spike Finding 2.4);
  - a **discovered** layer, populated through an injectable **`IAgentDiscoverySource`** seam that is
    **absent by default** (so refresh is a strict no-op until #493 wires `getChatChannelMembers` +
    author-scan into it);
  - a **moderate TTL** on discovered entries (spike Finding 3: negative ids look recreation-stable but
    are *not guaranteed*, so don't hard-cache them forever), **explicit refresh**, and
    **evict-on-write-failure** (drop a discovered entry whose cached id failed to resolve/notify, then
    re-discover);
  - persistence + workspace-keying + schema/clock guards mirroring `Services/StatusCache` /
    `ListColorCache` (own `StateKeys.AgentDirectories` document; per-entry epoch-ms `FetchedAtMs` so the
    TTL survives restarts).
- **`AgentIdentity.IsAgentId(id) => id < 0`** — the **single predicate** for the "negative id ⇒ agent"
  heuristic (spike: *heuristic, not a contract*), kept in one place rather than sprinkled through call
  sites (#494's own guidance). The registry filters both seeded and discovered entries through it, so a
  non-agent id never enters an agent directory.
- Pure logic split into a sibling static **`AgentDirectory`** helper (merge precedence, staleness
  partition), mirroring `AssigneeFrequency` / `ListFrequency`.

### Merge precedence

`Entries` = **seeded (in seed order), then discovered not already seeded (in discovered order)**, deduped
by id with **seed winning** on a collision. A manual pin is authoritative — it's how a user corrects or
overrides a stale/incorrect discovery. Eviction drops from the **discovered** layer only, so a transient
write failure never nukes a user's hand-pinned agent (that's a config edit, not an auto-drop).

## Deferred to #493 (kept tracked on #494 — this does **not** `Closes #494`)

- **Wiring a real `IAgentDiscoverySource`** onto `getChatChannelMembers` (+ channel author-scan) — needs
  the v3 chat client #493 owns, whose route (extend the curated spec vs. a small hand-written v3 client)
  is an **open maintainer decision**. The spike's recommendation is a hand-written `ClickUpChatClient`
  (Option 2), but that's #493's call.
- **Live construction in `Program.cs` + threading into the consumer.** Nothing reads the registry yet
  (the agent-aware mention picker is #495; the negative-id write path is #490 D, gated on #577's live
  probe), so this slice deliberately ships no dead runtime wiring — exactly as #603's facade shipped
  without its TUI. The config seed surface is live so a user *can* pin agents today; the code that
  reads it into a constructed cache lands with the first consumer.

## Acceptance criteria coverage

| #494 AC | This slice |
| --- | --- |
| Directory populates, persists, refreshes on TTL & on demand, evicts a failing entry | ✅ seed + discovered layers; `StateKeys.AgentDirectories` persistence across restarts; `NeedsRefresh`/`RefreshAsync`; `Evict` |
| Manual config seed works with discovery unavailable | ✅ `SuperAgentSettings.Agents` → cache seed; `IAgentDiscoverySource` null ⇒ refresh no-op, seed still served |
| Pure logic (staleness, eviction, merge) unit-tested | ✅ `AgentDirectory`/`AgentIdentity`/`AgentDirectoryCache` tests |
| `dotnet test` green | ✅ |
| Discovery *source* wired | ⛔ deferred → #493 (seam shipped) |

## Invariants respected

- No `Generated/` hand-edits, **no spec change / Kiota regen** — this slice touches no ClickUp endpoint.
- No TUI change — no second focusable pane, no new keybinding (#3/#12).
- ClickUp auth quirk untouched.
- New cache key added to `CacheReset` (a logout forgets it) — pinned by `CacheResetTests`.
- Config seed added behind an unconditional null-coalesce guard in `ConfigMigrations` (a hand-edited
  `"superAgents": null` degrades to defaults, matching the `AgentDispatch`/`TaskWorkingDirectories`
  guards) — no version bump, since there is no legacy data to fold.
