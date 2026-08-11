# Plan — Checklists: multi-tab nudge sync for checklist writes (#519)

Follow-up to #457 (the `Space`-toggle checklist-item write, part of the Task Checklists epic #453).
#457's facade `ClickUpClient.SetChecklistItemResolvedAsync` deliberately shipped **without** a
change-marker nudge: there was no checklist entry in the field taxonomy and the write response is the
parent checklist (`{ checklist }`), carrying no task `date_updated` to seed a marker. So a checklist
toggle in one tab only surfaces in another tab of the same task on that tab's next 30 s auto-refresh,
not immediately.

## Decision

Take the **wire-the-nudge** path (the acceptance criteria's first branch), not the "record a decision
that the auto-refresh latency is acceptable" fallback. Immediacy is the higher-value outcome and — as
the investigation below shows — the consumer machinery already delivers it for free once a marker is
recorded, so the added surface is tiny (one taxonomy entry + one `Record` call + a threaded `taskId`).

## What already exists (the consumer side needs no change)

The cross-process nudge channel (#294/#295) is a *notification, not a replica*: a writer records a
`ChangeMarker` keyed by `taskId`; every other running instance's `ChangeMarkerConsumer.Advance` scan
emits that id and the host re-fetches **just that task**. Crucially, the consumer is entirely generic —
it never branches on `ChangedFields` (those are advisory/diagnostic only):

- **`ChangeMarkerConsumer.Advance`** re-fetches any in-view task. A marker with a **null**
  `ServerDateUpdatedMs` is *never* suppressed (the suppression guard requires a server time to compare),
  so it always re-fetches — exactly the `ListsFields`/`CommentFields` "empty body ⇒ always re-fetch"
  precedent.
- **`TodoApp`**: `IsNudgeTaskInView` counts an open Task Detail (`_screens.OfType<TaskDetailScreen>()`)
  as in view, and `ReconcileNudgedTask` already calls `RefreshDetail(screen, taskId)` for every open
  detail of a nudged task — a full detail re-fetch that includes `Checklists`.
- **`SingleTaskApp`**: `PollMarkers` → `RefreshTab(_root)` on any nudge for the seed task, via
  `SingleTaskNudgePolicy`.

So the only gap is the **producer**: the checklist facade records no marker. Fix that and both hosts
reflect a cross-tab checklist toggle on the next poll tick without waiting for the full auto-refresh.

## Change (producer only)

- **Field taxonomy** (`ClickUpClient`): add `private static readonly string[] ChecklistFields =
  ["checklist"]`, alongside `StatusFields`/`ListsFields`/etc. Advisory-only, consistent with the rest.
- **Facade** (`IClickUpClient` + `ClickUpClient`): `SetChecklistItemResolvedAsync` gains a leading
  `taskId` parameter (matching the taskId-first convention of `SetTaskStatusAsync`/`AddTaskToListAsync`)
  and, after the confirmed 2xx write, calls
  `_changeMarkers.Record(taskId, serverDateUpdatedMs: null, ChecklistFields)`. Null server date because
  the `{ checklist }` response carries no task `date_updated` — same shape as the membership/comment
  writes. The 2xx gate is inherent (a non-2xx throws in `Guard(...)` before the `Record`).
- **Service** (`TaskService`): thread `taskId` through the passthrough.
- **Hosts** (`TodoApp`, `SingleTaskApp`): pass the task id they already hold at the call site
  (`resolvedId` / the tab's task id) into the callback lambda. The `TaskDetailScreen`
  `setChecklistResolvedAsync` **callback signature is untouched** — the host closure supplies `taskId`.
  So there is **no TUI change** (no new keybinding, no render change, no second focusable pane).

## Why no `Generated/` or spec change

The write endpoint and its `{ checklist }` response already shipped in #457's spec/regen. This slice
adds only C# on the facade/service/host seam. No Kiota regen.

## Tests

- **Unit** (`ClickUpClientChangeMarkerTests`): a new case —
  `SetChecklistItemResolved_OnSuccess_RecordsChecklistMarkerWithNullServerDate` — asserts the recorded
  marker has the passed `taskId`, `ServerDateUpdatedMs == null`, and `ChangedFields == ["checklist"]`,
  plus a `FailedWrite_RecordsNoMarker` sibling proving the 2xx gate (a 4xx throws, records nothing).
- **Existing** (`ClickUpClientChecklistWriteTests`): update the two call sites for the new `taskId`
  parameter (a signature update, not a weakening) and add an assertion that the URL/body are unchanged.
- No integration/TUI change is required: the consumer path is already covered by the multi-tab nudge
  E2E (`nudge_two_instance_check.py`) and the detail refresh path, and this slice adds no rendering.

## Deferred / follow-up

The later checklist CRUD writes (E–G, #458–#460) should record the same `ChecklistFields` marker as
they land, for consistency across all checklist mutations. #458 is currently in flight in PR #535, so
this PR wires the **merged** toggle only; the E–G wiring rides with those slices (noted in the PR).
