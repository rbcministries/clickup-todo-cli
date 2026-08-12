# Plan — Contextual chords (E): task-rename facade + F2/Ctrl+E rename in Task Detail (#542)

Slice **E** of the contextual key/chord remapping epic (#537), implemented against the model
recorded in slice **A** (`docs/plans/contextual-chord-model.md`). Depends on **A** (the F2/Ctrl+E
design, merged as #538) and **B** (#539, `F2` freed — Settings → `F10`, merged).

## What #542 asks for

1. **Facade:** `SetTaskNameAsync(taskId, name, ct)` → `PUT /task/{id}` with
   `UpdateTaskRequest { Name = name }`, returning the server-confirmed name — mirroring
   `SetTaskStatusAsync` / `SetTaskDescriptionAsync`. **No spec change / Kiota regen** —
   `UpdateTaskRequest.name` already exists in the curated spec and generated client. Plus a
   `TaskService` passthrough.
2. **TUI:** on the non-checklist Task Detail tabs, `F2` renames the current task's title (a
   single-line rename overlay), with `Ctrl+E` per the A decision; optimistic update +
   revert-on-failure + flash; refresh the header (`TaskDetailFormatter.HeaderLines`) and the
   terminal-window title (#418/#425) on a confirmed rename.

## Scope of *this* PR: the facade half (part 1)

This PR ships **part 1 only** — the `SetTaskNameAsync` facade, its `IClickUpClient` declaration,
and the `TaskService` passthrough, fully unit- and integration-tested. This is the exact non-UI
deliverable the model note isolates for E:

> *"…slice E adds only a `SetTaskNameAsync` facade + `TaskService` passthrough — no spec change /
> Kiota regen."* — `contextual-chord-model.md` §3

It is self-contained, has no dependency on any other in-flight slice, is fully verifiable in CI
(no Terminal.Gui surface), and is the foundation that **both** E's Task-Detail rename UI and **H**
(#545, F2 rename in the main list / Task Tree) consume.

### Facade contract (mirrors `SetTaskDescriptionAsync`)

- `ClickUpClient.SetTaskNameAsync(string taskId, string name, CancellationToken ct = default)`:
  - Rejects `null` up front (`ArgumentNullException`) — Kiota omits a null typed property, so a
    null would send an empty body and silently no-op — and rejects blank/whitespace
    (`ArgumentException`): ClickUp has no concept of an empty task title, so failing fast
    client-side gives the future UI a clean validation seam and avoids a pointless round-trip.
  - `Guard("UpdateTask", …)` around `PUT /task/{id}` with `new UpdateTaskRequest { Name = name }`.
  - Records a confirmed-write change-marker nudge (#294/#519) on a new `NameFields = ["name"]`
    set, exactly as the other writes nudge their own field family.
  - Returns the **server-confirmed** `updated?.Name` (return-the-truth, like `SetTaskStatusAsync`).
- `IClickUpClient.SetTaskNameAsync` — a default *throwing* declaration so read-only fakes needn't
  implement a write path they never call (mirrors the other write declarations); `ClickUpClient`
  overrides it.
- `TaskService.SetTaskNameAsync` — a thin passthrough to the client, returning the server-confirmed
  value so a future detail view can reflect the rename without a manual refresh.

### Tests (this PR)

- **Unit** (`ClickUpClientWriteTests`, `CapturingHandler`): the write issues a `PUT /v2/task/{id}`
  with a string `name` body; the returned value is read back from the *response* (distinct from the
  request, proving it's the server's truth, not an echo); the body touches **only** `name` (not
  status / priority / description / assignees); `null` throws before any transport; blank/whitespace
  throws before any transport.
- **Integration** (`ClickUpClientIntegrationTests`, `SkippableFact`, `CLICKUP_TOKEN`+`CLICKUP_TASK_ID`
  gated): rename to a marker, assert the confirmed value and that a subsequent `GetTaskDetailAsync`
  reflects it, then restore the original name in a `finally` (idempotent) — mirroring
  `SetTaskDescription_RoundTripsThroughDetailFetch`.

## Deferred to a follow-up: the Task-Detail rename UI (part 2)

The `F2`/`Ctrl+E` Task-Detail rename overlay is **deliberately deferred** to a follow-up that builds
on slice **C (#540)**, for two concrete reasons:

1. **It plugs into C's seam, which is not on `main` yet.** The model note (§2, §5) has slice **C**
   introduce the `DetailSubContext` enum + the per-tab activation table + `ResolveDetail` — the seam
   E's `F2` wiring and footer hint are designed to consume. C is currently an **open, unmerged PR
   (#586)**. Implementing E's UI on `main` today would mean either duplicating C's infrastructure
   (guaranteed conflict + two divergent copies of the sub-context model) or wiring `F2` imperatively
   in a way the epic then has to unpick — neither is a clean slice.
2. **The issue body and the model note disagree on what `F2` does on non-item tabs**, and the
   difference is a real product decision, not a detail:
   - #542 body: *"on the non-checklist Task Detail tabs, `F2` renames the current task's title (a
     single-line rename overlay)."*
   - `contextual-chord-model.md` §3: *"Every other tab (Comments / Description / Other / Stream) —
     no per-row item, so `F2` is a pure alias of `Ctrl+E`"* (i.e. opens the description editor).

   The UI slice should pin this (title-rename overlay vs. description-editor alias, and whether
   `Ctrl+E` gains a distinct title-rename mode) before it is built, alongside C's merged seam.

**Tracking:** #542 stays **open** after this PR (this PR does *not* `Closes #542`); it remains the
tracker for part 2. This PR links it and records the deferral so the next run picks up the UI half
cleanly once #540 lands.

## Hard-rules compliance

- **No `Generated/` hand-edits, no spec change, no Kiota regen** — `UpdateTaskRequest.name` already
  exists.
- **ClickUp auth quirk untouched.**
- **Logic in a testable service** — the write lives in the `ClickUpClient` facade with unit +
  `SkippableFact` integration coverage; no test is weakened.
- **No TUI change in this PR**, so no second focusable pane and no latency concern (#3); the UI half
  will honor those invariants when it lands.
