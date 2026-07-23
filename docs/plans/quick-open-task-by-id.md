# Quick-open a task by ID / custom ID / URL (Ctrl+O) — #303

Standalone UX feature (not part of the #292 multi-tab epic). Press **Ctrl+O** from the
main list, type/paste a **task ID**, **custom ID**, or **task URL**, and jump straight to
that task's Task Detail view. Depends on nothing unmerged; the new-tab variant
(Ctrl+Enter → `--task`, needs #296/#301) stays a follow-up.

## Acceptance criteria (from the issue)

- Ctrl+O opens an entry surface; a valid task ID, custom ID, or URL (`app.clickup.com`
  or the workspace subdomain) opens that task's Task Detail.
- A **cached** task opens without a resolve round-trip. An **uncached custom id** flashes
  **"Fetching task…"** (the resolve step) then the existing **"Loading details…"**; an
  uncached plain id has no separate resolve step, so it shows just **"Loading details…"**.
- An unresolvable/invalid input flashes an error and leaves the current screen unchanged
  (no navigation).
- URL/custom-ID parsing + resolution-order logic is **pure and unit-tested**; `dotnet
  test` green, then `tui-validate` covers open-by-id, open-by-URL, the uncached flash
  sequence, and the not-found error path.

## Design

Pure model + thin host glue + one new client call, mirroring the repo's
`RowHitTester` / `SubtaskArranger` split (Terminal.Gui-free logic in `Services/`, glue in
`Tui/`).

### 1. Pure parser + cache resolution — `Services/QuickOpenParser.cs`

- `enum QuickOpenKind { TaskId, CustomId, Invalid }`
- `readonly record struct QuickOpenRef(QuickOpenKind Kind, string Value)` with
  `Invalid` / `Task(id)` / `Custom(id)` factories.
- `QuickOpenParser.Parse(string? input) -> QuickOpenRef`:
  - Trim; empty ⇒ `Invalid`.
  - Normalise a scheme-less `*.clickup.com/…` paste by prefixing `https://`.
  - If it parses as an absolute http(s) URL:
    - host not `clickup.com` / `*.clickup.com` ⇒ `Invalid` (a foreign web link — accept
      **any** ClickUp subdomain per the issue's open question, since #304's stored
      subdomain isn't required to parse the path).
    - else parse the path: find `/t/`, split the trailing segments —
      1 segment ⇒ `TaskId`, 2+ segments ⇒ `CustomId` (`…/t/{team_id}/{custom_id}`),
      no `/t/` ⇒ `Invalid` (a ClickUp URL that isn't a task link).
  - Otherwise a bare token: contains `-` ⇒ `CustomId` (`ABC-123`), else `TaskId`
    (`86abc123`) — ClickUp's own id/custom-id convention.
- `QuickOpenParser.FindInCache(IReadOnlyList<TaskItem> universe, QuickOpenRef r) -> TaskItem?`:
  match by exact `Id` first, then by `CustomId` (case-insensitive) — the issue's
  cache-first resolution order ("by task ID, then by CustomId"). `Invalid` ⇒ null.

### 2. Custom-ID API lookup (spec + regen + facade)

The generated `GET /v2/task/{task_id}` builder only exposes `include_subtasks`. To resolve
an **uncached** custom ID we need `custom_task_ids=true&team_id=…`.

- **Curated spec** (`ClickUp/clickup-openapi.json`): add `custom_task_ids` (boolean) and
  `team_id` (string) query params to `GET /v2/task/{task_id}`. Regen with
  `dotnet kiota generate …` (per `scripts/regen-client.ps1`; `pwsh` is unavailable in the
  web env, run kiota directly). No `Generated/` hand-edits.
- **Facade** `ClickUpClient.GetTaskDetailByCustomIdAsync(customId, teamId, ct)` → maps via
  the existing `MapDetail` (ClickUp returns the task with its **real** `id`, so the result's
  `Id` is the plain id we then open). Added to `IClickUpClient` as a **default-throwing**
  member so existing test fakes compile unchanged (repo convention, cf. `AddTaskToListAsync`).
- **TaskService** passthrough.

### 3. Entry surface — `Tui/Screens/QuickOpenScreen.cs`

A modal `Screen` (simpler than a Dispatch-style pane; consistent with New Task / Quick
Updates). Single `TextField` + **Open** (default) / **Cancel** buttons. Enter/Open with
non-blank text raises `Submitted(text)` and closes; blank text flashes an inline hint
without closing; Esc cancels. No second focusable pane in the main list (#3) — it's a
full-window modal over the list, same as the other screens.

### 4. Host glue — `TodoApp`

- Bind **Ctrl+O** in `OnListKey` (guarded on `ActiveScreen is null`, like Ctrl+N).
- `OpenQuickOpen()` shows `QuickOpenScreen`; on `Submitted`, `ResolveAndOpen(text)`:
  - `Parse` ⇒ `Invalid` → flash an error, no navigation.
  - `FindInCache(CandidateUniverse(), r)` hit → `OpenTaskDetail(cached.Id)` (its existing
    "Loading details…").
  - Uncached **TaskId** → `OpenTaskDetail(r.Value)` directly (its own "Loading details…"
    IS the fetch — there's no separate resolve step, so no redundant "Fetching task…"; a
    not-found surfaces as a flashed error, no navigation).
  - Uncached **CustomId** → flash "Fetching task…" (the resolve round-trip, visible while
    the lookup is in flight), off-thread `GetTaskDetailByCustomIdAsync(r.Value, WorkspaceId)`,
    then `OpenTaskDetail(detail.Id)` on the UI thread; failure flashes an error, no
    navigation. (One extra GET on a cold custom-id open — the resolve fetch — is negligible
    for a one-shot open.)
  - Blank `WorkspaceId` on the custom-id path → flash "Workspace not configured".
- **Footer**: add `Ctrl+O open` to `HelpItemSets.MainList`; add a `QuickOpen` set for the
  entry screen.

## Invariants preserved

- **Generated client / curated spec** — the only API change is the curated spec + regen;
  no `Generated/` hand-edits, no auth change (raw `Authorization`).
- **No second focusable pane (#3/#38)** — the entry surface is a full-window modal screen,
  not an extra pane on the list.
- **Bare letters reserved for type-ahead (#12)** — Ctrl+O is a chord.

## Test plan

- **`QuickOpenParserTests`** — `Parse`: bare id, hyphen custom id, `app.clickup.com/t/{id}`,
  subdomain `odbm.clickup.com/t/{id}`, custom-id URL `…/t/{team}/{custom}`, scheme-less
  paste, trailing slash, query/fragment stripped, non-clickup URL ⇒ Invalid, non-task
  clickup URL ⇒ Invalid, empty/whitespace ⇒ Invalid. `FindInCache`: id hit, custom-id hit
  (case-insensitive), miss, invalid.
- **Facade** (offline capturing handler): `GetTaskDetailByCustomIdAsync` sends
  `GET /v2/task/{customId}?custom_task_ids=true&team_id=…` and maps the real id; an API
  error surfaces as `ClickUpApiException`.
- **`tui-validate`** `quick_open_check.py`: Ctrl+O opens the entry surface; open-by-id and
  open-by-URL reach Task Detail; the uncached path flashes "Fetching task…"; a bogus id
  flashes an error and stays on the list.

## Deferred (tracked)

- **New-tab variant** (Ctrl+Enter / Ctrl+Left-Click → `clickup-todo --task <id>` in a new
  process tab) — needs #296's `--task` and #301's terminal-spawn helper. Follow-up.
- **Ctrl+O from the Task Detail view** — this slice binds it on the main list only.
