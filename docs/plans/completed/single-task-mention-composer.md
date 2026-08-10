# Single-task @-mention authoring (SingleTaskApp composer) — #473

Follow-up to #325, which wired the @-mention picker into the `Ctrl+N` comment
composer **only in the dashboard host** (`TodoApp`). In single-task launch mode
(`SingleTaskApp`, `clickup-todo --task <id>`) the composer's three mention seams
(`postStructuredCommentAsync`, `memberMatch`, `memberTopFrequent`) are left
`null`, so `@` types a literal `@` and comments post as plain text.

`TaskDetailScreen`'s composer glue is host-agnostic — it lights up the moment a
host supplies those three seams (`MentionEnabled` = all three non-null). So this
is purely a **host-wiring gap** in `SingleTaskApp`.

## Approach — reuse `AssigneeFrequencyCache` (issue option b)

The issue offers two ways to supply a member pool. I take **option (b): give
`SingleTaskApp` an `AssigneeFrequencyCache` like the dashboard has**, rather than
inventing a second roster type on `TaskService` (option a). Rationale:

- It is the **exact same type** the dashboard projects into the picker, already
  unit-tested (`AssigneeFrequencyCacheTests`, `AssigneeFrequencyTests`), with the
  sync `Match` / `TopMostFrequent` the picker seams need — no new async→sync
  bridge to write and test.
- Its constructor already **loads any persisted pool** for the workspace from the
  shared state store, so a single-task tab opened after the dashboard has run
  gets a **warm, frequency-ranked** pool for free.
- When no persisted pool exists (single-task mode has no working-set frequency
  signal), its one-shot `TopUpAsync` fetches the workspace members and seeds them
  at count 0 — exactly the issue's expected **raw-roster fallback**, ordered by
  name.

This keeps the two hosts on one pool implementation and one projection, so they
can't drift.

## The pure, testable piece

The dashboard projects `AssigneeFrequencyCache` results (`TaskAssignee`) into the
`WorkspaceMember(Id, Name, null)` shape the picker consumes — inline, twice
(`memberMatch` / `memberTopFrequent`). Adding a third host would make four copies
of that projection. Extract it once:

- **`Tui/MentionMemberProjection.ToMembers(IReadOnlyList<TaskAssignee>)`** — pure,
  Terminal.Gui-free, returns `IReadOnlyList<WorkspaceMember>` with
  `Username ⇐ Name`, `Email = null` (so `WorkspaceMember.DisplayName` renders the
  name). Unit-tested for the mapping, empty input, and id/name preservation.

Both `TodoApp` and `SingleTaskApp` call it, removing the duplication.

## Phases

1. **Projection helper + de-dup.** Add `MentionMemberProjection`, unit-test it,
   and refactor `TodoApp`'s two inline projections to use it (no behaviour
   change; `mention_check.py` must stay green).
2. **Host wiring.** Add an optional `AssigneeFrequencyCache? assignees = null`
   parameter to `SingleTaskApp`. When present:
   - Wire the three composer seams into the `TaskDetailScreen` construction in
     `BuildDetailTab`, keyed to *this tab's* task id (mirroring the plain
     `postCommentAsync`), so a task opened by walking the Task Tree tab (#374)
     writes to its own task.
   - Kick the one-shot `TopUpAsync(10)` off the UI thread once on boot (mirrors
     `TodoApp`'s deferred top-up), so the pool fills from the workspace members
     when nothing rode along persisted.
   - When the cache is absent (`null`), all three seams stay `null` → `@` is a
     literal `@`, byte-identical to today (keeps a cache-less construction, e.g.
     a future test host, working).

   Thread the cache in from both hosts of the real app: `Program.cs` (the
   `--task` branch) and the E2E harness `Program.cs`.
3. **E2E.** Extend `mention_check.py` with an `E2E_SINGLE_TASK` leg: boot
   `SingleTaskApp` straight into the launch task's detail, `Ctrl+N` → `@` → search
   `Ada` → `Enter` inserts `@Ada Lovelace` → post → assert the structured
   `{"type":"tag","user":{"id":101}}` block in the recorded body and the token
   rendered in the composer + pane. A plain comment in the same leg still posts
   via `comment_text`. The dashboard leg stays unchanged.

## Acceptance criteria (from #473)

- `--task` mode: `Ctrl+N` → `@` opens the picker; a picked member posts a real
  ClickUp mention (structured `comment` tag block), exactly as the dashboard.
- Plain comments in single-task mode still post via the text path unchanged.
- `dotnet test` green (projection unit tests); `tui-validate` covers the
  single-task mention path.

## Non-goals / notes

- No spec / `Generated/` / facade change — the structured write path (#322) and
  the picker (#324) already exist; this only wires them into a second host.
- Single sectioned `ListView` invariant untouched; the picker is the existing
  `SelectorView`-based overlay from #324, not a new focusable pane.
- Single-task mode still has no `Ctrl+E` description-mention until #326/#512
  lands; this issue is scoped to the `Ctrl+N` comment composer, as the issue
  states. When #512 merges, the description editor's `@` will light up in
  single-task mode too, for free, off the same seams.
