# E2E harness: a specificity-ranked route table for `FakeClickUp`

Issue #488 (E2E **D**), sub-issue of the E2E harness epic #484. **Independent**;
foundation for **E** (#489, per-scenario route contribution).

## Problem

`FakeClickUp.SendAsync` (`tests/ClickUpTodo.Tui.E2E/Program.cs`) routes every
request through one hand-ordered `if / else if` chain (~140 lines). Two costs:

1. **Shared ordered chain — a merge-conflict hotspot.** Every scenario that adds
   an endpoint inserts a branch into the same construct, so scenario PRs collide
   here (append point 5 of the epic).
2. **Order is load-bearing and silent.** A generic `path.Contains("/task/")`
   branch sits *after* every more-specific `/task/…` branch. Insert a specific
   route below it and the request is silently swallowed by the catch-all — a
   wrong response, not a build error. Nothing enforces or documents the ordering.

## Approach

Replace the chain with a **specificity-ranked route table** whose result does
not depend on registration order.

### Route model (`tests/ClickUpTodo.Tui.E2E/Routing.cs`, reusable + unit-testable)

```csharp
public sealed record Route<THandler>(HttpMethod Method, string Pattern, THandler Handler);
public sealed class RouteTable<THandler>       // ctor validates; Resolve(method, path) → THandler?
```

- **Pattern** is slash-delimited segments: literals plus `{placeholder}` segments
  that match any single path segment (`list/{listId}/task/{taskId}`).
- **Matching is suffix-anchored** on path *segments*: a pattern matches the
  *trailing* N segments of the request path, so the `/api/v2` base prefix is
  ignored and — crucially — `task/{id}` matches `/api/v2/task/T` but **not**
  `/api/v2/task/T/comment` (the second-to-last segment isn't `task`). That
  segment anchoring is what dissolves the `:372` catch-all hazard: the generic
  `task/{id}` can no longer swallow a longer `/task/{id}/comment` path.
- **Specificity** = (more literal segments wins, then more segments). Registration
  order is irrelevant; a specific route always beats a generic one.

### Fail loudly on ambiguity (the safety net)

Specificity ranking trades a *visible* ordering hazard for an *invisible* one
unless ties are rejected. The `RouteTable` **constructor throws** when two routes
for the same method tie on specificity (equal literal- and segment-counts) *and*
could match a common path (position-by-position compatible), naming both:

```
Ambiguous E2E routes: GET /task/{id}/x and GET /task/{y}/x have equal specificity.
```

Different-length or different-method routes never tie (segment count / method
break it), so genuinely distinct routes coexist; only true unresolvable overlaps
are rejected.

## The routes (one per current branch, behaviour preserved exactly)

| Method | Pattern | Handler (unchanged response builder) |
| --- | --- | --- |
| GET | `user` | canned user |
| POST/DELETE | `list/{listId}/task/{taskId}` | membership add/remove (#242) → `{}` |
| POST | `task/{id}/comment` | create-comment echo (+ `E2E_COMMENT_LOG`) |
| GET | `task/{id}/comment` | `CommentsJson` |
| POST | `comment/{id}/reply` | create-reply echo (+ `E2E_REPLY_LOG`) |
| GET | `comment/{id}/reply` | `RepliesJson` |
| PUT | `task/{id}` | foreign / nudge / assignee+description PUT echo |
| GET | `task/{id}` | detail (tmissing/PROJ123 404s; tree/foreign/nudge) |
| GET | `team/{id}/task` | team tasks (+ #333 closed-stall) |
| POST | `list/{id}/task` | create-task echo (+ `E2E_CAPTURE_FILE`) |
| GET | `list/{id}/task` | empty task list |
| GET | `list/{id}/field` | custom-field definitions (#249/#395/#446) |
| GET | `list/{id}` | `ListJson` |
| GET | `team` | teams + members |
| *(no match)* | — | `{}` (the old trailing `else`) |

Handlers return a full `HttpResponseMessage` so the `task/{id}` GET 404 paths
(`tmissing`, hyphenless `PROJ123`) stay intact. `SendAsync` collapses to: resolve
→ invoke handler → (no match) `{}`. It contains **no** `else if` path chain.

The method previously assigned to the two method-agnostic branches (`/user`,
the `/task/{id}` catch-all) is fixed to **GET** — the only method the harness ever
issues to them — so every real request resolves to exactly one route.

## Tests

- `RouteTableTests` (unit, `ClickUpTodo.Tests`, new `ProjectReference` → the E2E
  project):
  - Ambiguity assertion throws for equal-specificity same-method routes
    (`task/{a}/x` vs `task/{b}/x`), message names both.
  - Non-overlapping equal-specificity routes coexist (no throw).
  - Different methods on the same pattern coexist.
  - **Order-independence / `:372` regression:** a generic `task/{id}/{action}`
    registered *before* a specific `task/{id}/comment` still resolves the specific
    handler; and a bare `task/{id}` does **not** match `/task/T/comment`.
  - Prefix-independence (`/api/v2/…` vs `/v2/…`), method mismatch, and no-match →
    `default` cases.
- **tui-validate**: the real table is exercised end-to-end; A/B byte-identical
  checks stay byte-identical (`detail_check.py`, `color_check.py`), plus a spread
  covering each route family (comments, membership, PUT, team tasks, fields,
  reply threads, foreign, tree, nudge).

## Hard-rules check

- No `Generated/` edits, no `clickup-openapi.json`, no Kiota regen — test-harness
  only.
- No production/app code changes; no TUI/rendering change; single sectioned
  `ListView` model untouched.
- Independent of #522 (HarnessContext) and #513 (fixtures); all three edit
  `FakeClickUp`, so whichever lands first, the others rebase — inherent to the
  epic's "make these independently mergeable" goal.

## Deferred

- **E (#489):** `IE2EScenario` + reflection discovery lets each scenario register
  its own routes into this table. This PR keeps registration in one place.
