# Single-task launch mode — `clickup-todo --task <id>`

Issue: [#296](https://github.com/rbcministries/clickup-todo-cli/issues/296) (Multi-tab
sub-issue 4, epic #292). Dependency sub-issue (1) "harden `state.db`" is merged; this
is the first user-facing entry point of the multi-tab epic.

## Goal

Let the app boot straight into a single task's **Task Detail** view, so one task can
live in its own terminal tab — the mouse/keyboard equivalent of opening a task and
pinning that tab open. `clickup-todo --task <id>` authenticates as usual, fetches just
that one task + its comments, mounts `TaskDetailScreen` directly, and polls only that
task on the detail's existing 30s cadence — never building the main working-set list.

## Why this shape

- `TaskDetailScreen` is already fully decoupled (#17/#38): it takes plain data
  (`TaskDetail`, comments) + injected async write callbacks and raises events; it never
  reaches into the list or selection. So a single-task host just has to supply the same
  callbacks the dashboard does.
- The screen **owns its own 30s auto-refresh timer** (`OnShown` → `Application.AddTimeout`
  → `RefreshRequested`, removed on dispose). Wiring `RefreshRequested` → refetch-that-one-task
  gives "poll only the launch task on cadence" for free — no second `RefreshService`, no
  working-set fetch.
- A **dedicated minimal host** (`Tui/SingleTaskApp`) rather than teaching the dashboard's
  `TodoApp` shell a "no-list root" mode. The dashboard's screen seam
  (`ShowScreen`/`CloseScreen`) is load-bearing for the single-sectioned-`ListView`
  invariant (#3/#38); bending it to tolerate an absent `_frame`/`_list` root is exactly
  the kind of change that risks regressing the main list. An isolated host has zero blast
  radius on the dashboard and constructs only the "minimal service graph" the issue asks
  for (`TaskService` for the one task — no feed/focus/assignee/list caches).

## Scope (this PR)

1. **`--task <id>` / `--task=<id>` arg** parsed by a pure, unit-tested `TaskLaunchArg`
   (mirrors the existing `GetOption` shape but with explicit "flag present but value
   missing" semantics so a bare `--task` errors clearly). `--help` documents it.
2. **`Program.cs` branch:** after the shared token/setup + `GetMeAsync` + `TaskService`
   construction, if the launch flag carries an id, fetch the one task + comments, then run
   `SingleTaskApp` and return — constructing none of the dashboard-only services. A missing
   value or an unknown/unreachable id exits with a clear stderr message and a non-zero code,
   **before** the terminal is switched into the alt-screen.
3. **`Tui/SingleTaskApp`:** an `Application.Run` host that mounts the wired `TaskDetailScreen`
   as its root, with a bottom status/flash line + contextual help footer (rendered from the
   screen's own `HelpItems` via the shared `HelpLine`, re-fitted on resize). Wires:
   - `postCommentAsync` / `setDescriptionAsync` → `TaskService` (Ctrl+N / Ctrl+E work).
   - `RefreshRequested` → refetch the one task off-thread → `UpdateData` (F5 / 30s tick),
     coalesced so overlapping ticks can't pile up.
   - `OpenBrowserRequested` (Ctrl+B) → launch the browser, then quit.
   - `Closed` (Esc at the root) → quit (`Application.RequestStop`).
   - `HelpRequested` (F1) → stack a `HelpScreen` over the detail; Esc pops back.
   - Installs the same diff-flushing ANSI backend the dashboard does for snappy output.

## Deferred (out of scope, tracked)

- **Quick Updates in single-task mode (Ctrl+U):** the dashboard's Quick Updates write path
  reaches into the in-memory working-set snapshot `_all`, which a single-task tab doesn't
  build. Decoupling it is exactly **sub-issue (5) #297**. Until then, Ctrl+U in single-task
  mode flashes an explanatory message rather than silently no-op'ing.
- **Agent dispatch in single-task mode (Ctrl+A):** the dashboard's `DispatchAgent` is a
  large, `TodoApp`-coupled surface (prompt composition, working-dir resolution, terminal
  launch). Extracting it into a host-agnostic coordinator is its own refactor, unrelated to
  #296's acceptance criteria. Until then, dispatch in single-task mode flashes an
  explanatory message. Tracked as a **new follow-up issue**, linked from the PR.
- **Id forms:** accept the raw ClickUp API task id only for now (as the issue's open
  question resolves). Task-URL / custom-id parsing is a noted follow-up (shares the parser
  #316 adds).

## Tests

- **Unit (`TaskLaunchArgTests`):** absent flag; `--task <id>`; `--task=<id>`; bare `--task`
  (missing value); `--task=` / whitespace value (missing value); id trimming; flag among
  other args; first-wins on repeats.
- **TUI (not CI-unit-testable):** verified via build + a `tui-validate` scenario
  (`single_task_launch_check.py`) that boots the PTY harness with `--task <id>` against the
  fake backend and asserts the Detail view renders, Esc quits, and (as harness support
  allows) a description edit / comment post round-trips.
- Integration boundaries stay on the existing `SkippableFact` `TaskService` coverage.
