# Plan — #73 (part 1): resolve usernames/emails → assignee ids via a workspace-members lookup

## Scope of this slice

Issue #73 has three deferred pieces from #68/PR #72. This plan delivers **part 1 only** — the
cleanest, most self-contained, most testable, and downstream-unblocking piece:

> Resolve arbitrary usernames / emails → assignee ids so an `Assignee IS <name|email>` F3 rule
> contributes to the server-side fetch, instead of being silently skipped.

**Deferred (still tracked by #73, not closed by this PR):**

- Part 2 — `Assignee IS NOT` + client-side multi-assignee any/none matching. Entangled with the
  unconditional Personal-Tasks-list merge (a client-side assignee filter would wrongly drop
  Personal-list tasks owned by teammates). Needs task provenance threaded through the snapshot;
  a real design change, out of scope here.
- Part 3 — "everyone" (empty assignee set) broad-fetch UX polish (confirmation / cap).

## Key discovery

ClickUp v2 `GET /team` (the existing `GetAuthorizedTeams` operation) already returns each team with a
`members[]` array (`members[].user = {id, username, email}`). The curated spec's `Workspace` schema
only maps `id`/`name`, so members never reach the client. So the spec change is minimal: add a
`members` array to `Workspace` (+ a `Member` component) and regenerate — **no new path**, and it
reuses the call `GetWorkspacesAsync` already makes.

## Hard-rule compliance

- Spec edit is to the **curated** `clickup-openapi.json`, then `kiota generate` (verified: `pwsh`
  is absent but `dotnet kiota generate` with the pinned tool runs and is deterministic — a no-op
  regen produced zero diff). `Generated/` is never hand-edited.
- Auth quirk untouched. No new focusable pane (no TUI layout change). Tests land with the code.

## Phases

### Phase 1 — spec + client members fetch
- `clickup-openapi.json`: add `Member` schema `{ user: $ref User }`; add `members: [Member]` to
  `Workspace`. Regenerate the client.
- `Models.cs`: new domain record `WorkspaceMember(long Id, string? Username, string? Email)`.
- `ClickUpClient`: `GetWorkspaceMembersAsync(workspaceId, ct)` → fetch teams, pick the matching
  workspace (fallback: first), map its members via internal static `MapMembers` (drops id==0).
- Tests: `ClickUpClientMapTests.MapMembers_*` (offline, mirrors `Map` tests); a
  `SkippableFact` integration test `GetWorkspaceMembers_ReturnsMembersWithIds` gated on
  `CLICKUP_TOKEN`+`CLICKUP_WORKSPACE_ID`.

### Phase 2 — resolution + reload wiring
- `TaskService`:
  - New pure overload `ResolveAssigneeIds(view, currentUserId, IReadOnlyList<WorkspaceMember>)`
    that resolves `me` + numeric id **and** matches a name/email (case-insensitive) against
    member `Username`/`Email` → id(s). The existing 2-arg overload stays as the me+numeric-only
    fast path / members-fetch-failure fallback.
  - `HasUnresolvedAssigneeNames(view)` (pure) — true iff any `Assignee IS` value is neither `me`
    nor numeric, so we only pay the members round-trip when a name is actually present.
  - `ResolveAssigneeIdsAsync(view, ct)` — fast path when no names; else fetch members (memoized
    per service instance), best-effort (on failure fall back to me+numeric, allow retry).
  - `LoadAsync` uses `ResolveAssigneeIdsAsync`.
  - Replace `SameAssigneeSet` (id-based, can't see unresolved names) with
    `AssigneeRuleValues(view)` (the distinct, case-insensitive set of `Assignee IS` values) for the
    reload decision — any assignee-rule change reloads, which is correct because assignee drives the
    fetch; a name change is no longer missed.
- `TodoApp.ApplyViewSettings`: compare `AssigneeRuleValues(previous)` vs `(result)`; reload on
  change; keep the "may be slow" flash when the result set is empty (everyone).
- `ValidOps(Assignee)` stays IS-only (IS NOT is part 2).
- Tests: update the `PendingMembersLookup` test → resolves via members; add name/email/case/
  not-found/union cases; `HasUnresolvedAssigneeNames` cases; `AssigneeRuleValues` set-diff cases
  (replacing the `SameAssigneeSet` test).

## Verification

- `dotnet build -c Release` (0/0), `dotnet test -c Release` (integration skips w/o creds),
  `dotnet format`. TUI reload path verified by reasoning (documented in PR): F3 → type a teammate's
  username/email in an `Assignee IS` rule → reload fetches that teammate's tasks server-side.
