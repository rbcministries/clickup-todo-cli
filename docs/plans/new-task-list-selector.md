# New Task screen — List selector + cursor-seeded primary list (#240)

Sub-issue **L** of the Writing New Content epic (#208). Builds on the New Task
screen (#213/#215) and the reusable `ListSelectorView`/`ListSelectorModel`
(#239, merged in PR #285). Refines #213: the create target is no longer fixed to
`PersonalTasksListId` — it comes from the user's context and is user-changeable.

## Goal

Add list selection to the New Task screen and make the POST target come from the
selected **primary** list, seeded from where the user is:

- Opening New Task from a task row seeds the List selector with that row's list
  as the primary/home list.
- Opening from a header row (no cursor task), a context parent (#46), a foreign
  subtask (#70/#179), or a task with a blank list id falls back to the
  configured Personal Tasks list.
- The user can add/remove/change lists; Save creates the task in the **primary**
  (first-selected / home) list via `CreateTaskAsync(primaryListId, …)`.
- Removing every list blocks Save with a flash (≥1 list required).

Multiple-list membership (adding the created task to the extra selected lists) is
**out of scope** — it stays deferred to #241. This slice only writes the primary
list; the selector already allows selecting more, and `Primary` identifies the
one we POST to.

## Design

### Pure logic (unit-tested) — `NewTaskForm`

1. **`ResolveListSeed`** — pure resolver for the primary/home seed. Inputs: the
   cursor task's `(listId, listName)`, whether it's a context parent / foreign
   subtask, and the configured personal `(listId, listName)` fallback. Returns
   the `NamedEntity` to seed as primary: the cursor list when it's a real own
   row with a non-blank list id, else the personal fallback. The host supplies
   the context-parent / foreign-subtask booleans (it already knows them via
   `_contextParents` / `_foreignSubtasks`), keeping the whole fallback decision
   pure and testable.
2. **`TryBuild`** gains a `primaryListId` parameter and a new
   `ListRequiredError`. A blank primary list id fails validation. Precedence
   follows screen order: **name → list → due date** (name still wins over
   everything, matching the existing precedence test). `NewTaskRequest` itself is
   unchanged — the list is a separate path parameter to `CreateTaskAsync`, not a
   body field — so validation is centralized here while the screen reads the
   chosen list id from the selector's `Primary`.

### TUI glue (CI-untestable, validated via `tui-validate`) — `NewTaskScreen`

- Embed a `ListSelectorView` in `CollectSelection` mode, backed by
  `ListFrequencyCache.Match` / `TopMostFrequent`, seeded with the resolved
  primary as its `primary` (home) entry. It renders `" (home)"` on the primary
  and is single-focusable (no second focusable pane — #3/#38).
- Layout: insert the List label + selector between Assignees and the
  Priority/Due block. The 50-row PTY validation terminal has ample height; the
  Assignees selector shrinks its `Dim.Fill` reserve to make room, and the List
  block is anchored above the (still bottom-anchored) Priority/Due/Save block.
  Tab order: Name → Description → Assignees → **List** → Priority → Due →
  Save/Cancel.
- `TrySave` reads `_lists.Primary`, passes its id to `TryBuild` (blocks + flashes
  + focuses the List pane when none is selected), and on success POSTs via a
  `createAsync(primaryListId, request, ct)` callback — the create facade now
  takes the list id from the screen instead of a host-closed constant.
- Extend `HelpItemSets.NewTask` to mention the List pane.

### Host wiring — `TodoApp.OpenNewTask`

- Keep the existing "no Personal Tasks list configured" guard (guarantees a valid
  fallback exists).
- Compute the seed via `NewTaskForm.ResolveListSeed` from `CurrentTask()` and the
  `_contextParents` / `_foreignSubtasks` membership, with the config personal
  list as fallback.
- Pass the list `Match`/`TopMostFrequent` delegates (from `_lists`) and the seed
  into the screen; wire `createAsync` to `(listId, request, ct) =>
  _tasks.CreateTaskAsync(listId, request, ct)`.

## Phases

1. **Pure logic + tests.** `ResolveListSeed`, `TryBuild` list requirement,
   updated + new `NewTaskFormTests`. (Opens the draft PR.)
2. **TUI wiring.** `NewTaskScreen` List pane + layout + `TrySave`;
   `OpenNewTask` seed + delegates + createAsync; `HelpItemSets.NewTask`.
3. **E2E + validate.** Update `new_task_check.py` (List pane renders + seeded
   home + new Tab order) and run `tui-validate`; finalize + review.

## Acceptance criteria (from #240)

- [ ] New Task from a task row seeds the List selector with that row's list as
      primary; from a header/context/foreign/blank-list row it falls back to the
      personal list. (`ResolveListSeed` unit tests + tui-validate seeded home.)
- [ ] The user can change the list(s); Save creates in the primary list; removing
      all lists blocks Save with a flash. (`TryBuild` list-requirement unit tests
      + tui-validate.)
- [ ] `dotnet test` green (seed-resolution + validation unit-tested), then
      `tui-validate` confirms the seed, changing the list, and create-in-primary.

## Invariants

- Generated client / curated spec untouched — no new ClickUp API surface
  (`CreateTaskAsync` already takes the list id as a path parameter).
- Single sectioned `ListView` on the main list; the List selector is a single
  focusable composite embedded in the modal — no second focusable pane (#3/#38).
- Bare letters remain reserved for type-ahead (#12); no new bare-letter shortcut.
