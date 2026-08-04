# `--task` accepts every reference form Ctrl+O does (#464)

Widen the single-task launch flag (`--task`, #296) so it accepts **every**
reference the in-app Ctrl+O quick-open accepts — a plain task id, a workspace
**custom id** (`ABC-123`), and a **ClickUp task URL** (`…/t/{id}` and
`…/t/{team_id}/{custom_id}`) — resolving custom ids **cache-first** so the
common case is one correct request instead of a failed one followed by a retry.

This is the follow-up `TaskLaunchArg`'s own doc comment names ("URL / custom-id
forms are a noted follow-up"). The parser (#316), the cache-first lookup (#303),
and the custom-id-fallback service method already exist — this is wiring plus a
single new resolution seam, mirroring the in-app path so the two can't drift.

## Existing machinery reused (nothing new invented)

| Piece | Where |
| --- | --- |
| Reference parsing (id / custom id / URL incl. `/t/{team}/{custom}`) | `Services/QuickOpenParser.Parse` |
| Cache-first custom-id → task lookup | `Services/QuickOpenParser.FindInCache` |
| Custom-id API lookup | `IClickUpClient.GetTaskDetailByCustomIdAsync` |
| Plain-id-then-custom-id 404 fallback | `TaskService.GetTaskDetailWithCustomIdFallbackAsync` |
| Snapshot, already loaded before the launch branch | `Program.cs` `taskCache` |
| In-app resolution order to mirror | `TodoApp.ResolveAndOpen` (`:1369`) |

## Design

### 1. Parsing — `TaskLaunchArg` keeps "flag present but no value"; classification moves to the shared parser

`TaskLaunchArg.Parse` is unchanged: it still owns the distinct **`MissingValue`**
state (bare `--task` / `--task=` fails early with a clear message) and hands back
the raw trimmed token. `Program` then classifies that token through
`QuickOpenParser.Parse`, so `--task` and Ctrl+O share one classifier with no
duplicated logic.

### 2. Resolution — one new testable seam on `TaskService`

```csharp
Task<TaskDetail> ResolveLaunchTaskAsync(
    QuickOpenRef reference, IReadOnlyList<TaskItem> snapshot, string? teamId, CancellationToken ct)
```

resolves in the same order as the in-app path:

1. **Snapshot** — `QuickOpenParser.FindInCache(snapshot, reference)`. A hit yields
   the **plain** task id, so a single correct `GET /task/{id}` — no wrong-endpoint
   round-trip. **Stale mapping is not fatal:** if that GET 404s (task deleted /
   custom id reassigned) we fall through to the live path rather than failing.
2. **Live**, mirroring `ResolveAndOpen`'s kind branch:
   - a **custom id** (`ABC-123` / URL custom) → `GetTaskDetailByCustomIdAsync`
     (one correct request);
   - a **plain id** (incl. a *hyphenless* custom id misclassified as a plain id,
     #353) → `GetTaskDetailWithCustomIdFallbackAsync` (plain first, custom-id
     retry only on a 404) — which closes the hyphenless-custom-id hole for
     `--task` as a side effect.

The method takes the snapshot as a plain list (`FindInCache` is pure), so
`TaskService` keeps no `TaskCache` dependency and the seam is fully unit-testable
through the `IClickUpClient` fake.

### 3. Team-id precedence

`teamId = reference.TeamId ?? config.WorkspaceId` — a URL-carried team id wins
over the configured workspace, the exact precedence `TodoApp.ResolveAndOpen:1387`
uses.

### 4. Error messages (in `Program`, echoing what the user typed)

- **Unparseable** (`QuickOpenKind.Invalid`, e.g. `--task ???`) → fails **before**
  any setup/auth, with a message distinct from "didn't resolve".
- **Custom id + no configured workspace** → a message naming *that* cause, not
  "task not found".
- **Not found** (404 out of the resolver) → echoes the **typed** token, not the
  resolved id.
- Comments are fetched by the **resolved** `detail.Id`, never the typed token.

### 5. `--help`

The `--task` line names the accepted forms (id, custom id, URL).

### Left alone

`AppLaunchCommand.ForTask` (the Ctrl+Enter "open in a new terminal tab" gesture)
keeps emitting `--task {plain id}` — this issue widens what `--task` **accepts**,
not what the app **emits**.

## Tests

- **`TaskServiceLaunchResolveTests`** (new, mirrors `TaskServiceQuickOpenFallbackTests`'
  fake-client pattern, asserting call counts not timing):
  - snapshot hit for a plain id → 1 `GET /task`, 0 custom lookups;
  - snapshot hit for a custom id (incl. **hyphenless**) → resolved via the plain
    id, 1 `GET /task`, no 404-then-retry;
  - **stale** snapshot hit (plain GET 404s) → falls back to the live custom-id
    lookup instead of failing;
  - uncached plain id → 1 `GET /task`;
  - uncached **hyphenated** custom id → direct custom-id lookup (1 request);
  - uncached **hyphenless** custom id (parsed as a plain id) → 404 then custom-id
    retry;
  - URL-carried team id is threaded through in preference to the configured one;
  - an `Invalid` ref throws (guarded by `Program` before the call).
- The parser/classification is already covered by `QuickOpenParserTests`; no change.
- `Program`'s boot glue isn't unit-testable (top-level statements); verified by
  build + the `single_task_launch_check.py` PTY scenario staying green (the launch
  path's terminal output is unchanged for a plain id).
