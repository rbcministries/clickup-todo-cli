# Plan: Document & onboard the mention→assignee automation prerequisite (#126)

Part of the Mentions & Comments feed epic (#109).

## Problem

The Mentions & Comments feed (#114) synthesises an inbox client-side by fanning
out over comments on the tasks **assigned to** the user. ClickUp has no inbox /
mentions API, so a bare `@mention` on a task the user is *not* assigned to never
enters that pipeline. The validated workaround is a **per-Space ClickUp
Automation** that converts a mention (via the follower it creates) into an
assignment, which pulls the task into `GetAssignedTasksAsync` so the feed and
mention detection (#113) see it.

The app **cannot create, enforce, or even verify** this automation (no public
automations API). So it must be a clearly documented user prerequisite, and the
app should point the user at that documentation from the one place the feed is
introduced.

## Acceptance criteria (from the issue)

1. Docs describe the automation setup + **every** caveat (per-Space, permission-
   gated, paid, not-retroactive, blast-radius, unverifiable-via-API).
2. In-app help references the prerequisite where the feed is introduced.

## Approach

Pure docs + copy change. No ClickUp API surface, no spec/Kiota regen, no new
service. Nothing touches the list-row render path, and no new focusable pane or
keybinding — the #3/#38 latency and #12 type-ahead invariants are untouched by
construction.

### Phase 1 — Docs

- New `docs/mention-assignee-automation.md`: the exact automation
  (Trigger: *Comment added* · Condition: *Follower is any of `[you]`* · Action:
  *Update assignees → Add `[you]`*), a step-by-step setup, the validated
  evidence (the `EA-6737` check from the issue), and a **Caveats** section
  covering all six constraints verbatim in intent.
- README: a short "Mentions & Comments feed" subsection that links the doc, so
  the prerequisite is discoverable from the front page too.

### Phase 2 — In-app guidance + tests

The feed is "introduced" to the user in two framework-free places, both already
unit-tested as pure surfaces:

- `NotificationsFeedScreen.EmptyStatePlaceholder` / `.NoMentionsPlaceholder` —
  the empty-state copy. This is exactly where a user wonders *"why don't I see
  my mentions?"*, so it is the highest-value spot for the note.
- The global `HelpScreen` (F1 from the feed raises `HelpRequested`, which opens
  it) — one concise line by the existing `Ctrl+E` entry.

Design to keep existing tests valid (they compare `EmptyMessage(...)` against the
placeholder constants exactly):

- Add a shared `public const string MentionCoverageNote` to
  `NotificationsFeedScreen` (one short note, ending with the doc path).
- **Bake it into both placeholder constants** via compile-time `+`
  concatenation. `EmptyMessage(...)` therefore keeps returning the (now
  note-bearing) constant unchanged — the three existing `EmptyMessage_*` asserts
  and the two placeholder asserts still pass — while every empty state now
  carries the coverage note. No test is weakened or deleted.
- Add a line to `HelpScreen` near the `Ctrl+E` entry pointing at the doc.

### Tests (new)

- `MentionCoverageNote` is non-empty, names the per-Space automation, and cites
  `docs/mention-assignee-automation.md`.
- Both `EmptyStatePlaceholder` and `NoMentionsPlaceholder` **contain** the
  coverage note (so the guidance shows in every empty state, filtered or not).
- The `EmptyMessage(...)` result carries the note in all three branches.

TUI can't run in CI; the empty-state copy is asserted as a pure string. Manual
`tui-validate` (Ctrl+E → empty feed) is described in the PR for the maintainer.

## Out of scope / deferred

- A first-run modal or per-Space nag — the issue lists a first-run hint only as
  *optional*; the empty-state + Help + README coverage satisfies the AC without
  adding a new focusable surface. Not tracked as debt (explicitly optional).
- Any attempt to detect/verify the automation — impossible via the public API
  (documented as a constraint, not a gap).
