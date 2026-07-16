# Plan: tui-validate harness — seed foreign-subtask / context-parent rows for Quick Updates (#232)

## Goal

Close the automated-coverage gap #233 left open: prove end-to-end (under the PTY + pyte harness)
that Quick Updates opens and applies a Status edit on a **not-mine** row — a foreign subtask
(`_foreignSubtasks`, #70/#179) or a context parent (`_contextParents`, #46) — and that the edited
row shows the confirmed value **in place** and is **not dropped** from the list. This is the
behaviour #160 (PR #233) shipped but could not assert automatically because the fake backend
produced no such rows and didn't model a `PUT /task/{id}` write echo.

## Constraints

- **Opt-in via `E2E_FOREIGN=1`** so the default A/B byte-identical renders (color/detail/latency/
  volume checks) are undisturbed — when the flag is unset, Program.cs behaves exactly as today.
- No product-source changes: this is harness + drive-script only. The rows are produced entirely by
  the existing resolvers (`ResolveForeignSubtasksAsync` #87, `ResolveContextParentsAsync` #46) and
  the existing Quick Updates apply path (#156/#157/#160) — the harness only feeds them the right
  canned ClickUp responses.
- Never hand-edit `Generated/`; no curated-spec change (no new API surface).

## How the rows arise (verified against the source)

- **Foreign subtask**: `ResolveForeignSubtasksAsync` runs when F4 is on (`View.Subtasks != Hidden`).
  With a <=8-task snapshot it takes the **per-parent** path (`SubtaskFetchStrategy.Plan`), calling
  `GET /task/{id}?include_subtasks=true` (`GetSubtasksAsync`, reads `subtasks[]`) for each snapshot
  task. A returned subtask whose `parent` is in the snapshot but which is **not itself** in the
  snapshot is kept by `ForeignDescendants` and marked `(not assigned to you)` — provided it has an
  assignee (else it's `(unassigned)`; `SubtaskVisibility.IsUnassigned`).
- **Context parent**: `MissingParentIds` finds a snapshot task whose `parent` id is absent from the
  snapshot; `ResolveContextParentsAsync` fetches `GET /task/{parentId}` and injects it as a
  top-level header marked `(parent — not assigned to you)`, with its snapshot child nested under it.

## Scenario seed (Program.cs, all gated on `E2E_FOREIGN=1`)

Config: `View.Subtasks = All`, `BadgeDisplay = Text` (so a row's status shows as a readable word,
not a `(IP)` chip), ungrouped, no pins.

Fake `/team/{id}/task` snapshot (small → per-parent path; `last_page:true`), all in `plist`:
- `t1` "Aardvark parent task", status `to do` — parent of the foreign subtask.
- `t2` "Beta task under context parent", status `to do`, `parent = cpar` (absent → triggers context
  parent).

Fake per-path responses (foreign scenario only):
- `GET /task/t1?include_subtasks=true` → `t1` with `subtasks:[fsub]`, `fsub` = "Delta foreign
  subtask", `parent:t1`, `plist`, status `to do`, assignee `{999, "Casey Teammate"}` (→ not-mine,
  not unassigned).
- `GET /task/{other}?include_subtasks=true` → the task with no `subtasks` (empty).
- `GET /task/cpar` (no `include_subtasks`) → context-parent detail "Gamma context parent", `plist`,
  status `to do`.
- `PUT /task/{id}` → **echo** the requested `status` (and `priority`) parsed from the body so the
  Status/Priority round-trip is truthful (`SetTaskStatusAsync` reads `status.status`); keeps the
  existing assignee-mutation echo for parity.
- `GET /list/plist` → existing `ListJson` (5 statuses incl. `in progress`) — unchanged.

Resulting deterministic row order (default sort = due then name; `SubtaskArranger`):
```
row0  Aardvark parent task             (t1, mine, depth 0)
row1    Delta foreign subtask ...      (fsub, depth 1)  . (not assigned to you)
row2  Gamma context parent ...         (cpar, depth 0)  . (parent — not assigned to you)
row3    Beta task under context parent (t2, depth 1)
```

## Drive script: `foreign_quickupdates_check.py`

1. Boot; assert the list rendered and **both** not-mine markers are present (`(not assigned to you)`
   and `(parent — not assigned to you)`) — proves both rows seeded & visible.
2. `Down` → row1 (fsub). `Space` opens Quick Updates; assert the screen opened with the fsub name in
   its title (proves it's **not blocked**) and the block flash is absent. `Down`+`Enter` commits
   `in progress`. `Esc` back to the list; assert the fsub row now shows `in progress`, still carries
   `(not assigned to you)`, and is still present (not dropped).
3. `Down` → row2 (cpar). Repeat the open/commit/exit assertions for the context-parent row
   (`(parent — not assigned to you)`).

## SKILL.md

Add a check #5 documenting the opt-in scenario + drive script, mirroring the existing entries.

## Validation

- `dotnet build clickup-todo.slnx -c Release` (0/0) and `dotnet test` green (harness is a separate
  csproj, so the main suite is unaffected — but confirm nothing regressed).
- Build the harness (`e2e.csproj`) and run the **existing** checks with `E2E_FOREIGN` **unset** to
  confirm the default renders are byte-identical (regression guard for the opt-in gating).
- Run `foreign_quickupdates_check.py` → PASS.

## Deferred

- Priority/Assignees edits on not-mine rows: the harness now models the Priority echo too, but the
  drive script asserts Status only (the headline #160 case). A follow-up could extend it; not
  required to close #232's Status gap.
