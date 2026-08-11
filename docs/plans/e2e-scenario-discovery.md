# E2E harness: `IE2EScenario` + reflection discovery (one file per scenario)

Issue #489 (E2E **E**), the finale of the E2E harness epic #484. **Depends on**
C (#487, `HarnessContext`) and D (#488, `RouteTable`) — both merged. Removes the
epic's **append points 3 and 4**: the top-level scenario-flag block and the
`FakeClickUp` env-flag properties. After this, adding a scenario is *structurally
incapable* of conflicting — it is one new file nobody else's diff touches.

## Problem

Scenario state and selection live in two shared anchor blocks in
`tests/ClickUpTodo.Tui.E2E/Program.cs`:

- **Top-level flags** (`var foreign = Env("E2E_FOREIGN") == "1"` …) — every
  scenario appends one here.
- **Env-flag properties inside `FakeClickUp`** (`static bool CustomFieldsMany =>
  Env("E2E_CUSTOM_FIELDS_MANY") == "1"` …) — same anchor problem.

Both are one-liners at a shared anchor, which adjacent insertions can never
auto-merge. D already moved routing to a specificity-ranked `RouteTable`; C moved
constructor params to `HarnessContext`. What remains is scenario *selection and
state*.

## Approach — discovery, not registration

**A scenario is one file.** It self-activates from its own legacy env var(s),
tweaks the `AppConfig`, contributes routes, and does any pre-boot setup — all in
that file and nowhere else.

```csharp
interface IE2EScenario
{
    string Name { get; }                       // matches E2E_SCENARIO; also its handle
    bool IsActive { get; }                      // reads its own legacy env var(s)
    void Configure(AppConfig config) { }        // default no-op
    void SeedBackend(FakeClickUp backend) { }   // seed shared mutable state (assignees, …)
    IEnumerable<Route<RouteHandler>> Routes(FakeClickUp backend) => [];  // D's route type
    Task BeforeBootAsync(HarnessContext ctx) => Task.CompletedTask;
    IAppHost? Host => null;                      // non-null takes over app boot (single-task/feed)
}
```

**Discovery by reflection, never a registry** (Program.cs):

```csharp
var all = typeof(Program).Assembly.GetTypes()
    .Where(t => !t.IsAbstract && typeof(IE2EScenario).IsAssignableFrom(t) && t != typeof(DefaultScenario))
    .Select(t => (IE2EScenario)Activator.CreateInstance(t)!)
    .ToList();
var selector = Environment.GetEnvironmentVariable("E2E_SCENARIO");
if (selector is { Length: > 0 } && all.All(s => s.Name != selector))
    FailFast(selector, all);                     // prints discovered names, exits non-zero
var active = all.Where(s => s.IsActive || s.Name == selector).ToList();
```

`DefaultScenario` is **always active** and holds the shared generated backend
(200-task generator, statuses/members/list names, the default detail/comment
builders, the base routes). Active opt-in scenarios layer over it.

### Why route override works (the priority tier)

D's `RouteTable` resolves by specificity, but two scenarios overriding the same
endpoint register the **same pattern** (`GET task/{id}`), which ties. The fix is a
one-field extension to `Route`/`RouteTable`: a **priority tier**. Default routes are
tier 0; an active scenario's routes are tier 1. `Resolve` prefers the highest tier
among matches, then specificity. The ambiguity assertion runs **within a tier**, so
two tier-0 routes still can't tie, and a scenario cleanly overrides a default.

Two *active* scenarios overriding the same endpoint would tie at tier 1 and throw —
which is correct: that combination is unsupported. **Audit of all 39 checks confirms
no check ever activates two scenarios that override the same endpoint** (task GET,
task PUT, team tasks, comments, list fields). So every real invocation resolves
cleanly; the throw only guards a genuinely ambiguous future combination.

Augmenting scenarios (title-refresh, qu-lists, checklists, seed-assignee) override
the endpoint they touch and **reuse the Default builder**, patching its result — no
duplicated response bytes, no drift. `FakeClickUp` exposes its builders (`DetailJson`,
`TasksJson`, `CommentsJson`, `ListFieldsJson`, …) and shared mutable state for reuse.

### App host (single-task / feed)

`E2E_SINGLE_TASK` and `E2E_FEED` change *which* app boots. A scenario returns a
non-null `Host`; Program boots the first active scenario's host, else the default
dashboard. The host gets the constructed services (a small `HarnessServices` bag),
so the three app entry points move out of the top-level flag block too.

## Backwards compatibility

The 39 checks pass `E2E_TREE=1`, `E2E_NUDGE=1`, `E2E_CUSTOM_FIELDS_MANY=1`, … and
are documented that way in `SKILL.md`. **Those invocations keep working unchanged**
— each scenario maps its own legacy variable(s) in `IsActive`. `E2E_SCENARIO` is an
*additive* selector, not a replacement. Rewriting check invocations is out of scope.

`E2E_TASKS` / `E2E_REFRESH` stay base config (they parameterize the Default backend,
not a scenario).

## Scenarios extracted (one file each, under `Scenarios/`)

Overrides: `ForeignScenario` (#232), `TreeScenario` (#291), `NudgeScenario` (#376).
Augments: `SeedAssigneeScenario` (#234), `TitleRefreshScenario` (#425),
`QuListsScenario` (#365), `ChecklistsScenario` (#456–459), `CustomFieldsScenario`
(#249/#395/#446), `DescriptionLinkScenario` (md-link #430 / wrap-split #443),
`ThreadsScenario` (#329), `LongStreamScenario`/`VaryCommentsScenario` (#468),
`CommentLogScenario` (#325), `ReplyLogScenario` (#330), `CaptureFileScenario` (#395).
Config/pre-boot: `RichViewScenario`, `LinkCtrlDestScenario` (#320),
`WarmClosedScenario` (#333), `SubdomainScenario` (#304), `BrowserLogScenario` (#304),
`TabLogScenario` (#320), `MarkerDbScenario` (#376), `SingleTaskScenario` (#296, host),
`FeedScenario` (#509, host).

`DefaultScenario` keeps the generated backend + base routes.

## Fail-fast

Unknown `E2E_SCENARIO` prints the discovered scenario names and exits non-zero —
turning a mistyped name from a silent no-op into an actionable error. Unit-tested.

## Tests

- **Unit (`ClickUpTodo.Tests`):**
  - Discovery selects the right active set from env (a fake env), incl. legacy vars
    and `E2E_SCENARIO`.
  - Unknown `E2E_SCENARIO` → fail-fast (message names discovered scenarios).
  - `RouteTable` priority tier: a tier-1 route overrides a tier-0 route of identical
    pattern; ambiguity still throws *within* a tier; tier-0 pair still throws.
  - The **one-file property**: reflection discovers every `IE2EScenario`; the concrete
    Default table, and Default+each-single-scenario table, build without ambiguity.
- **tui-validate:** the full check suite. A/B byte-identical checks
  (`detail_check.py`, `color_check.py`) stay byte-identical. This touches the harness
  boot path, so terminal validation is mandatory (CLAUDE.md) — after `dotnet test`.
- **Acceptance probe:** add a scratch scenario file, confirm `git status` shows
  exactly one new file (documented in the PR; the scratch file is not committed).

## Hard-rules check

- Test-harness only: no `Generated/` edits, no `clickup-openapi.json`, no Kiota regen.
- No production/app code changes; no TUI/rendering change; single `ListView` untouched.
- Lands against an **empty PR queue** (the sequencing warning in #489/#484) — the
  precondition this issue calls out, satisfied at pick time.

## Sequencing / deferred

- #564 (a PTY check for the dispatch working-dir browser seed) is explicitly gated
  behind this issue — it becomes a one-file scenario afterward.
- If budget forces a stop, the framework + extracted-so-far is a green, coherent
  slice: "no central list" and "new scenario = one file" hold from Phase 1 on; any
  not-yet-extracted knob stays a `DefaultScenario` internal, tracked as follow-up.
