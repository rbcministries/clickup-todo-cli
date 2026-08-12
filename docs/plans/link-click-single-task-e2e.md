# Fix `link_click_check.py` single-task leg (#567)

**Issue:** [#567](https://github.com/rbcministries/clickup-todo-cli/issues/567) — the
`single_task_checks()` leg of `tests/ClickUpTodo.Tui.E2E/link_click_check.py` fails.

## What the issue reported

`single_task_checks()` boots `SingleTaskApp` and immediately does `app.click_url(TASK_URL)`,
assuming the seeded task-link URL is on the boot frame. Since the Stream default (#106) the
single-task detail renders comments first with the **description below the fold**, so the URL
isn't visible and `click_url` asserts *"…86a1b2c3d not on screen"*. The suggested fix: add a
`cycle_to(TASK_URL)` step (as `dashboard_checks()` already has) before clicking.

## What the investigation actually found

The below-the-fold navigation is only the *first* of two stale assumptions in this leg — it dies
there before exposing the second. The leg (and the issue's own note that a plain click "opens the
browser, #374") encodes **pre-#374** behavior:

- **#318** (`task-detail-link-mouse-activation`) wired mouse link activation. At that time
  single-task mode sent *both* plain and Ctrl clicks on a task link to the **browser**, because it
  had "no in-app task→task destination **yet** (#374)".
- **#374** (`task-tree-tab-single-task-mode`) then *added* that destination. The recorded decision
  (#401 "Esc = Back") is to **stack** the linked task's detail over the launch task, uniform with
  the dashboard (#291). `SingleTaskApp.ActivateLink` now routes a plain task-link click to
  `OpenTaskDetail` (in-app, stacked); a **Ctrl**+click still follows the configured Ctrl
  destination (browser by default, #320).

So the current, deliberate behavior is **plain click → in-app stacked**, **Ctrl+click → browser** —
the opposite of the leg's "both → browser" expectation. The fixture resolves the link's id
(`86a1b2c3d`) to a task whose content mirrors the launch task, which is why a naive screen-text
check can't see the stack (the same reason `single_task_tree_check.py` proves stacking by
**Esc-depth**, not content).

## Fix (test-only)

Update `single_task_checks()` and the module docstring to assert the real #374 behavior:

1. `cycle_to(TASK_URL)` before the first click (the #567 navigation fix).
2. **Plain click** → no browser launch, the tab stays alive, and a single `Esc` **walks back** to
   the launch task's detail (proving a child was stacked — an inert click would instead hit the
   launch-task root and raise the #299 exit confirmation).
3. **Ctrl+click** → the browser (the link is already visible on the walked-back Description tab), the
   tab stays alive.

No app code changes — the behavior under test is correct and intentional; only the test's stale
expectation and docstring were wrong.

## Validation

- `dotnet build -c Release` — 0 warnings / 0 errors.
- `dotnet test -c Release` — green (3508 passed, 27 integration skipped without `CLICKUP_TOKEN`).
- `dotnet format --verify-no-changes` — clean.
- `tui-validate` — `link_click_check.py` both legs pass end-to-end under the PTY harness (repeated
  runs stable).
