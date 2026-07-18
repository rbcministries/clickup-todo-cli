# Plan — #323: full-name ↔ userId resolution + member display-name enrichment

Part of the Task Detail & Comments epic (#313); the **foundation for J** (the @-mention picker,
#324) and the write paths in K/L (#325/#326). The @-mention UX is name-driven ("Ben Seymour") but
the ClickUp mention payload needs the numeric `userId`; today there is no name→id mapping and no
first-class human-friendly name on the member roster.

## Key discovery (answers the issue's "determine where ClickUp exposes a display name")

ClickUp's `GET /team` returns each member as `{ user: { id, username, email } }`, and **`username`
is already the spaced display name** a user types in ClickUp (e.g. "Ben Seymour"), not a handle.
ClickUp's user object has no separate first/last/display field. So:

- **No spec change and no Kiota regen is needed** — the spaced name is already on the generated
  `User.username`, already mapped into `WorkspaceMember.Username`.
- What's missing is (a) a **guaranteed-non-blank** human name for the picker to render (some
  members have a null username and only an email), and (b) a **name → userId resolver** for the
  mention write path.

## Scope

1. **`WorkspaceMember.DisplayName`** — a computed, always-non-blank human name for the picker:
   the spaced ClickUp display name (`Username`), else the email's local part, else `User {Id}`.
   Implemented as a computed property on the record, so **no positional-constructor change** and no
   call-site / equality churn (existing `MapMembers` tests keep passing).
2. **`MemberResolver`** (new pure static service in `Services/`) — name → `userId` resolution:
   - `ResolveId(WorkspaceMember)` → `member.Id`: the picker path (#324) supplies a chosen member,
     so its id is used directly — no matching, no ambiguity.
   - `ResolveId(IReadOnlyList<WorkspaceMember>, string name)` → `long?`: exact, case-insensitive,
     trimmed match against `DisplayName`, `Username`, or `Email`. Returns the first matching
     non-zero id, or `null` when nothing matches. **Deliberately no fuzzy/substring matching** in a
     write path — a wrong id would @-mention the wrong person.

Out of scope (kept tight, tracked elsewhere): the picker UI itself (#324), wiring into the comment
composer (#325) / description editor (#326), and touching the F3 `Assignee IS` filter path
(`TaskService.MatchMembers`) — that filter already matches `Username`/`Email`, and `DisplayName`
derives from `Username`, so behavior is unchanged; leaving it out avoids scope creep.

## Hard-rule compliance

- **No `Generated/` hand-edits and no regen** — the needed field already exists on the generated
  model (see discovery above).
- Auth quirk untouched. **No TUI change** (no new focusable pane) — this slice is model + service +
  tests only.
- Tests land with the code: pure resolution/derivation logic is fully unit-testable with xUnit.

## Phases

### Phase 1 — model + service + tests
- `ClickUp/Models.cs`: add the computed `DisplayName` property to `WorkspaceMember` with the
  username → email-local-part → `User {Id}` fallback and doc comment referencing #323.
- `Services/MemberResolver.cs`: the two `ResolveId` overloads above, documented.
- Tests:
  - `MemberResolverTests` — display-name resolution (exact match on each key, case-insensitivity,
    trimming, no-match → null, ambiguity → deterministic first, blank/null input → null) and the
    picker `ResolveId(member)` id path.
  - Extend `ClickUpClientMapTests` (or a new `WorkspaceMemberTests`) to assert `DisplayName`
    derivation across the three fallback tiers.
- Quality gate: `dotnet build -c Release` (0/0), `dotnet test -c Release` green, `dotnet format`.

## Acceptance-criteria mapping

- *Given a member selected by display name, the code yields the correct userId* → `MemberResolver`.
- *The member roster carries a human-friendly full-name field* → `WorkspaceMember.DisplayName`.
- *`dotnet test` green (pure resolution logic unit-tested)* → Phase 1 tests.
