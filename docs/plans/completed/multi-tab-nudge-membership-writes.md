# Multi-tab nudge channel — membership-write markers (#348)

Part of the multi-tab epic (#292); follow-up to the nudge-channel **writer** #294
(PR #347, merged). The writer emits a change marker after the confirmed writes #294
listed — status, priority, assignee add/remove, description save, comment post — but
**deliberately excluded** the task↔list membership writes
(`AddTaskToListAsync` / `RemoveTaskFromListAsync`, #237), which were outside #294's
stated write set (see `completed/multi-tab-nudge-channel-writer.md` → "Out of scope").

Adding/removing a task from a list (the "Tasks in Multiple Lists" ClickApp) changes what
another tab's list-membership views show, so for full cross-tab convergence those writes
should nudge too. This plan closes that gap.

## The gap

`ClickUpClient.AddTaskToListAsync` / `RemoveTaskFromListAsync` run their write inside
`Guard(...)` but do **not** call `_changeMarkers.Record(...)` after the confirmed write —
unlike the five write types wired in #294. So a membership change in tab A leaves tab B
unaware until B's next full resync (~10 poll cycles), instead of the ~4 s nudge the other
write types get.

## Approach

Mirror the #294 emission discipline exactly, at the same choke point (the facade):

- Emit a marker **after** the `await …PostAsync` / `…DeleteAsync` inside `Guard`, so it
  runs **only on a confirmed 2xx** — a non-2xx throws before reaching it, giving the
  "failed write emits nothing" AC for free (identical to the other writes).
- `ServerDateUpdatedMs` = **`null`**. The membership endpoints
  (`POST`/`DELETE /list/{list_id}/task/{task_id}`) return an **empty body**, so there is
  no `date_updated` to carry — same as the comment-post case in #294. A null server date
  means "consumer can't suppress, always re-fetches": safe.
- `ChangedFields` = **`["lists"]`** (the advisory hint the issue specifies). Diagnostics
  only — the consumer re-fetches everything.
- Keyed by **task id** like every other marker (one row per task, upsert supersedes). The
  current marker model is task-keyed; whether the consumer (#295) needs to also invalidate
  a list-scoped view is a consumer-side concern to settle there, out of scope here.

No new endpoint, no curated-spec change, no `Generated/` edit — the membership endpoints
already exist (#237). This is a facade-emission addition plus unit tests only.

## Components

- `ClickUp/ClickUpClient.cs`
  - New `private static readonly string[] ListsFields = ["lists"];` alongside the existing
    `StatusFields` / `PriorityFields` / … hint arrays.
  - `AddTaskToListAsync` / `RemoveTaskFromListAsync` each call
    `_changeMarkers.Record(taskId, serverDateUpdatedMs: null, ListsFields)` after the
    confirmed write (inside the `Guard` lambda, after the `await`).

## Tests

Mirror `ClickUpClientChangeMarkerTests` (offline, real generated client over a fake
`HttpMessageHandler`, recording `IChangeMarkerStore`):

- `AddTaskToList_OnSuccess_RecordsListsMarkerWithNullServerDate` — task id stamped, null
  server date, `ChangedFields == ["lists"]`.
- `RemoveTaskFromList_OnSuccess_RecordsListsMarker` — same for the delete path.
- `AddTaskToList_FeatureDisabled_RecordsNoMarker` — a non-2xx (HTTP 400 `OV_016`) throws
  and records nothing (the 2xx gate).
- `RemoveTaskFromList_ApiError_RecordsNoMarker` — same for the delete path (HTTP 404).
- No-marker-store path already covered by the existing membership tests
  (`ClickUpClientListMembershipTests`, which build the client without a store); a
  `Record`-throws-swallow test is unnecessary — the store contract already swallows and
  the facade doesn't wrap `Record` (matching #294's other write paths).

## Out of scope

- Consumer-side handling of a `["lists"]` nudge (list-scoped view invalidation) — a #295
  consumer concern.
- Membership writes from the New Task multi-list create / Quick Updates list pane are
  shipped **disabled pending the list-change migration** (#365); this marker emission is
  correct for whenever those paths are enabled and for any direct facade caller today.

## Phase

One phase — the change is a two-line emission addition plus its unit tests. Build / test /
format green, push (draft PR opens), mark ready.
