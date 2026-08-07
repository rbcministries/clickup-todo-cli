# Plan — #417: Parallelize the Task Tree descendant BFS (bounded concurrency)

Issue: [#417](https://github.com/rbcministries/clickup-todo-cli/issues/417). Follow-up to the Task
Tree tab (#291); a perf win here also benefits the single-task-mode tree (#374).

## Symptom & root cause

Opening the **Task Tree** tab on a parent with many subtasks is slow — ~30s for 13 subtasks —
because `TaskService.GetTaskTreeAsync` gathers the tree with **strictly serial** ClickUp
round-trips:

- **Ancestry walk** — one `GetTaskItemAsync` per level up the parent chain (`await` in a `while`
  loop, capped at `MaxAncestorFetches = 10`).
- **Descendant BFS** — one `GetSubtasksAsync` per parent, `await`ed one at a time inside the BFS
  loop (capped at `MaxTreeSubtaskFetches = 25`).

So a tree with *N* subtask-bearing nodes costs *N* **serial** HTTP calls; at ~1–2s each that lands
around 30s.

## Scope of THIS slice (issue idea #1 — parallelize the BFS)

Turn the descendant BFS from strictly serial into **level-batched, bounded-concurrency** fetches. The
concurrency *bound* tracks `CommentThreadLoader.DefaultMaxConcurrency` (the reply-thread fan-out's
cap), but the *model* is deliberately different: those loaders roll a `SemaphoreSlim` window over a
fixed input list, whereas this awaits a whole frontier batch before starting the next — the batch
form is what lets a dynamically-growing BFS frontier reproduce the serial walk's fetch order and
de-dup exactly (see the invariant below). This directly collapses the dominant cost: *N* serial calls become
`ceil(N / concurrency)` sequential rounds (e.g. 13 subtasks → ~2 rounds at concurrency 8, instead
of ~13 serial calls).

**The ancestry walk stays serial** — each parent id is only known after the previous level's fetch
returns, so it is an inherent linked-list traversal with nothing to parallelize. It is also capped
at 10 and typically 0–3 deep, so it is not the reported bottleneck.

### The invariant that constrains the design

`TaskTreeArranger` → `SubtaskArranger.Arrange` **preserves the incoming order among siblings**, and
the descendant de-dup is **first-occurrence-wins**. So a naïve "fetch the whole level, append in
completion order" would reorder siblings and change which parent claims a shared child — breaking
`TaskTreeArrangerTests` / `TaskServiceTaskTreeTests` and, worse, silently changing the rendered
tree. The parallel BFS must therefore reproduce, **exactly**, the serial BFS's:

1. **Fetched-parent set** under the `MaxTreeSubtaskFetches` budget — the first *25* nodes in FIFO
   (breadth-first) order, no more.
2. **Descendant list order** — breadth-first, parents in FIFO order, children in returned order.
3. **De-dup** — first occurrence wins, seeded with the ancestry ids (a subtask echoing an ancestor
   or the current task is dropped and never re-BFS'd).
4. **Best-effort per branch** — a failed `GetSubtasksAsync` skips that branch (its slot yields no
   children) but **still counts against the budget** (the serial code does `fetches++` before the
   `try`); only a genuine `OperationCanceledException` propagates.

### How the batch loop preserves all four

Keep the single global FIFO `Queue<string>`. Each round:

- Dequeue a batch of `min(MaxTreeSubtaskConcurrency, MaxTreeSubtaskFetches - fetches, queue.Count)`
  parents **from the front** and add the batch size to `fetches` up front. Dequeuing from the front
  and stopping at the budget yields the identical first-25-in-FIFO fetched set (1).
- Fetch the batch concurrently (`Task.WhenAll`); each fetch is wrapped so a non-cancellation failure
  yields `null` for that slot (best-effort, 4).
- Fold the results back **in the batch's FIFO order**, applying the `descSeen.Add` de-dup and
  enqueuing new children as we go — so descendant order (2) and first-occurrence de-dup (3) match
  the serial walk byte-for-byte. A batch may straddle a BFS-level boundary; taking from the front of
  one global queue handles that naturally (FIFO already emits a whole level before the next).

New constant: `internal const int MaxTreeSubtaskConcurrency = 8;` (mirrors
`CommentThreadLoader.DefaultMaxConcurrency`).

## Tests (CI-verifiable — pure service logic through the `IClickUpClient` seam)

Extend `TaskServiceTaskTreeTests` (in-memory fake, no token):

- **All existing tests stay green unchanged** — they pin order, de-dup, caps, best-effort, and
  `SubtaskCalls` sequence, which the batch loop reproduces exactly.
- **New — fetches a frontier concurrently:** a level with several siblings is observed with >1
  `GetSubtasksAsync` in flight at once (deterministic gate; no timing/`Task.Delay`).
- **New — respects the concurrency bound:** a frontier wider than `MaxTreeSubtaskConcurrency` never
  exceeds the bound in flight.
- **New — budget cap holds across a wide level:** a level wider than the remaining budget fetches
  exactly the first `MaxTreeSubtaskFetches` parents in FIFO order (re-confirms cap under batching).

No TUI change (the loader is host-agnostic and already awaited off the UI thread), so no
`tui-validate` is required for this slice; the tab's behaviour and output are unchanged.

## Deferred (tracked separately)

Issue #417 lists two further ideas that are larger and TUI-coupled; they are **not** in this slice
and will be tracked by a follow-up issue linked from the PR:

- **Idea #2 — seed from the in-memory snapshot.** Prime the tree from the main-list working set /
  already-resolved F4 subtask relationships, hitting the API only for nodes not in hand. Needs a new
  seam to pass the snapshot (or a lookup) into `GetTaskTreeAsync` and a TUI caller change.
- **Idea #3 — progressive rendering.** Render levels as they resolve / surface progress instead of
  one "Loading task tree…" placeholder. TUI-coupled, `tui-validate`-only.
