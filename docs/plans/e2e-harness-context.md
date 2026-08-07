# E2E (C): replace `FakeClickUp`'s constructor parameter list with a harness context (#487)

Sub-issue **C** of the E2E harness epic (#484). Independent; foundation for **E**
(#489). A pure refactor of the PTY-harness host
(`tests/ClickUpTodo.Tui.E2E/Program.cs`) — no behavioural change, no new
scenarios, no routing changes.

## Problem

`FakeClickUp`'s boot-time scenario state is passed as a **positional constructor
parameter list**, and every scenario that needs boot state has to edit *two*
single lines:

- the constructor signature (`Program.cs:185`):
  ```csharp
  sealed class FakeClickUp(int taskCount, bool foreign = false, bool tree = false, bool checklists = false)
      : HttpMessageHandler
  ```
- the sole construction site (`Program.cs:80-81`):
  ```csharp
  var client = new ClickUpClient(
      "fake-token", new HttpClient(new FakeClickUp(taskCount, foreign, tree, checklists)), changeMarkers: changeMarkers);
  ```

Because each is a *single line*, two PRs that both append a parameter can never
auto-merge — git has no sub-line merge. The list has already accreted `foreign`
(#232), `tree` (#291) and `checklists` (#456), and would have taken more from the
current queue. This is the highest-value / lowest-effort structural fix in the
epic: two lines that today *guarantee* a conflict become two lines nobody edits
again.

## Proposal

Introduce a context object that carries scenario state, and pass **it**:

```csharp
/// <summary>Boot-time scenario state for the harness backend. New scenario state lands
/// here as a new property — an <em>additive</em> surface, not a positional list, so two
/// PRs adding properties merge cleanly (a property block still shares an anchor, so this
/// is "usually merges", not "never conflicts" — E (#489) finishes the job by moving
/// scenario state out of the shared type entirely).</summary>
sealed class HarnessContext
{
    public int TaskCount { get; init; } = 200;
    public bool Foreign { get; init; }
    public bool Tree { get; init; }
    public bool Checklists { get; init; }
}

sealed class FakeClickUp(HarnessContext ctx) : HttpMessageHandler
```

- `taskCount` is threaded **through** the context (per the issue), not kept as a
  separate positional parameter.
- The three instance flags keep their existing private backing fields
  (`_foreign`, `_tree`, `_checklists`), now assigned from `ctx.Foreign` etc., so
  the ~7 use-sites inside the class (`SendAsync`, `DetailJson`, `TasksJson`, the
  foreign/tree builders) are **untouched** — the change is confined to the
  constructor and the one construction site.
- The construction site builds the context from the top-level env-derived locals
  that already exist (`taskCount`, `foreign`, `tree`, `checklists`), so those
  lines (#487 out of scope; they are #489's append points 3/4) are unchanged.

### Why not go further now

Making additions truly *conflict-free* (each scenario owning its own state in its
own file) is exactly **E** (#489), which depends on this. C's job is only to
convert an **unmergeable single-line** change into a **mergeable multi-line** one.
Keeping the `_foreign`/`_tree`/`_checklists` fields (rather than reading
`ctx.Foreign` at every use-site) keeps this diff minimal and the behaviour
provably identical.

## Scope

- `Program.cs`: add `HarnessContext`; change the `FakeClickUp` primary
  constructor to take it; assign the three backing fields and use `ctx.TaskCount`
  where the old `taskCount` primary-ctor parameter was read (the `TasksJson`
  call in `SendAsync`); build and pass a `HarnessContext` at the construction
  site.
- Nothing else. No spec/Generated/facade/app change (this is test-harness-only).

## Acceptance

- The construction site and the `FakeClickUp` signature contain no
  scenario-specific positional parameters — state travels in `HarnessContext`.
- All checks pass; the A/B byte-identical checks (`detail_check.py`,
  `color_check.py`) remain byte-identical — this refactor is invisible to every
  rendered frame.
- `dotnet build -c Release` 0 warnings / 0 errors; `dotnet test -c Release`
  green; `dotnet format --verify-no-changes` clean.

## Tests / validation

The harness host **is** test code; its correctness contract is that the existing
`tui-validate` checks still pass (byte-identically for A/B). There is no new
domain logic to unit-test — `HarnessContext` is a plain init-only DTO. Validation
therefore drives the checks that exercise each of the four params threaded
through the new context:

- `color_check.py` + `detail_check.py` — A/B byte-identical (default `taskCount`,
  the full render path).
- `screen_check.py` / `drive.py` — `TaskCount` (E2E_TASKS=200) paging + volume.
- `foreign_quickupdates_check.py` — `Foreign` (E2E_FOREIGN=1).
- `tree_tab_check.py` — `Tree` (E2E_TREE=1).
- `checklist_check.py` — `Checklists` (E2E_CHECKLISTS=1).

If any of the four params were mis-threaded through the context, its scenario
check would fail (or the A/B would diverge), so this subset is sufficient to
prove the refactor invisible.
