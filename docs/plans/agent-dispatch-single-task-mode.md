# Plan — enable agent dispatch (Ctrl+A) in single-task launch mode (issue #345)

Follow-up to **#296** (single-task launch mode, `--task <id>`, epic **#292** sub-issue 4).
`SingleTaskApp` boots straight into one task's `TaskDetailScreen`. Agent dispatch (Ctrl+A) is
currently **deferred** there — `AgentDispatchRequested` is wired to an explanatory flash
("Agent dispatch isn't available in single-task mode yet.") — because the dashboard's dispatch
path (`TodoApp.DispatchAgent` + `RunBackgroundDispatch`, ~130 lines) is `TodoApp`-coupled.

## Acceptance criteria (from the issue)

- Ctrl+A from a `--task <id>` tab composes + launches a `claude` session (interactive **and**
  one-off), with the same working-dir / post-to-Comments / launch-location semantics as the
  dashboard.
- No behaviour change to the dashboard's existing dispatch.
- `dotnet test` green; the shared coordinator is unit-tested where logic is extractable.

## What already exists (leverage, don't rebuild)

The dispatch orchestration is already backed by **pure, unit-tested** helpers:

- `AgentDispatchSettings.ResolveEffectiveWorkingDirectory` / `ResolveWorkingDirectory` /
  `UsesTaskDerivedOutput` — working-dir precedence (#91/#95/#96/#98).
- `DispatchWorkingDirectoryCache.Update` / `PreFill` — per-task working-dir cache (#96).
- `AgentPromptComposer.OutputSubdirectoryToken` — the `./{custom-id}` subdir token (#98).
- `SettingsForm.ExpandHomePath` / `ResolveDefaultWorkingDirectory` — path expansion (pure statics).
- `AgentDispatcher.DispatchAsync` / `DispatchBackgroundAsync` — the (already unit-tested) launch seam.

What is **not** yet factored out is the *glue* that sequences these: the resolution block at the
top of `DispatchAgent`, plus the interactive `Task.Run` → `DispatchAsync` → Flash flow and the
one-off `AgentRunScreen` mount → `DispatchBackgroundAsync` flow (`RunBackgroundDispatch`). That
glue is duplicated-by-necessity if `SingleTaskApp` reimplements it, so it is exactly what to extract.

## Design

Add `src/ClickUpTodo/Tui/DispatchCoordinator.cs` — a host-agnostic coordinator both hosts drive.
It holds no Terminal.Gui view state, so the resolution half is unit-testable; the execution half
marshals via the same `Application.Invoke` pattern the inline code uses today.

### 1. Pure planning (unit-tested)

```csharp
public readonly record struct ResolvedDispatch(
    string Prompt, string? WorkingDir, string? OutputSubdir, string? Template,
    bool UseTaskDerived, bool OneOff, bool PostToComments, LaunchLocation LaunchLocation,
    string? ChosenDir, string? ResolvedDefault);   // last two feed cache reconciliation

public static ResolvedDispatch Plan(
    AgentDispatchSettings settings, DispatchRequest request, TaskDetail detail,
    string? defaultWorkingDirectory, string home);
```

`Plan` is a verbatim lift of the resolution block in `DispatchAgent` (lines ~1828–1864): expand
the pane pick, resolve the effective working dir, decide task-derived output + subdir, capture the
template, and compute `resolvedDefault` for cache reconciliation. No mutation, no I/O.

`ReconcileCache(cache, taskId, plan)` is a thin wrapper over `DispatchWorkingDirectoryCache.Update`
so both hosts reconcile + persist identically (host owns the `ConfigStore.Save`).

### 2. Shared execution (CI-untestable glue, relocated verbatim)

```csharp
public static void RunInteractive(AgentDispatcher agent, TaskDetail detail,
    IReadOnlyList<CommentItem> comments, ResolvedDispatch plan, Action<string> report);

public static void RunBackground(AgentDispatcher agent, TaskDetail detail,
    IReadOnlyList<CommentItem> comments, ResolvedDispatch plan,
    Action<AgentRunScreen, Action> mount, Action clearDispatching);
```

- `RunInteractive` fires the `Task.Run` → `Directory.CreateDirectory` (task-derived) →
  `agent.DispatchAsync(...)` flow and reports the status text back through `report` (the host
  supplies `msg => { _dispatching = false; Flash(msg); }`).
- `RunBackground` creates the `CancellationTokenSource` + `AgentRunScreen`, wires
  `CancelRequested`, calls the host's `mount(screen, onClosed)` (the host's `ShowScreen`), then runs
  `agent.DispatchBackgroundAsync(...)` streaming progress into the screen — identical to
  `RunBackgroundDispatch` today. `clearDispatching` is the host's `_dispatching = false`.

The only host-specific seams are `report`, `mount`, and `clearDispatching` — small delegates over
each host's Flash / ShowScreen / guard. No dispatch logic is duplicated.

### 3. `TodoApp` — drive the coordinator (no behaviour change)

`DispatchAgent` becomes: re-entrancy guard → `Plan(...)` → `ReconcileCache(...)` + `Save` →
branch on `plan.OneOff` to `RunBackground` / `RunInteractive`. `RunBackgroundDispatch` is deleted
(its body moved into the coordinator). Net behaviour identical; the diff is a straight relocation.

### 4. `SingleTaskApp` — wire it up

- Thread `ConfigStore` in via the ctor (Program.cs already has it in scope) so the per-task
  working-dir cache can persist.
- Build an `AgentDispatcher` once in `Build()` from `config.AgentDispatch` (there is no F2 settings
  dialog in single-task mode, so it never needs rebuilding — unlike `TodoApp`).
- Add a `_dispatching` guard mirroring `TodoApp`.
- Add an `Action? onClosed` param to `SingleTaskApp.ShowScreen` so `RunBackground`'s `mount` can
  cancel the CTS on close (parity with `TodoApp.ShowScreen`).
- Replace the deferral flash: `AgentDispatchRequested += (_, req) => DispatchAgent(req);`, where
  `DispatchAgent` mirrors `TodoApp`'s new thin version against the single task.

The detail screen is already constructed with the dispatch defaults (`defaultSessionMode`,
`defaultPostToComments`, `defaultLaunchLocation`, `workingDirectoryPreFill`) in `SingleTaskApp.Build`,
so the Dispatch pane already offers the right options — only the submit handler was missing.

## Tests

- `DispatchCoordinatorTests` (new, xUnit): `Plan` for each `AgentWorkingDirectory` mode
  (TaskDerived with/without an explicit pick → subdir on/off; Home; Fixed), one-off vs interactive,
  post-to-comments + launch-location pass-through, and `~`-expansion of an explicit pick. Assert the
  resolved values match the dashboard's current behaviour (the fields `DispatchAgent` computed inline).
- `ReconcileCache` delegates to the already-tested `DispatchWorkingDirectoryCache.Update`; add one
  test that the plan's `ChosenDir`/`ResolvedDefault` reach it correctly.
- Execution flows (`RunInteractive`/`RunBackground`) are Terminal.Gui/process glue — not
  CI-testable, same as the code they replace; covered by `tui-validate` + manual verification.

## Manual / TUI verification (not CI)

- `clickup-todo --task <id>`, Ctrl+A, type a prompt, Enter → interactive terminal launches (parity
  with the dashboard); one-off toggle → `AgentRunScreen` renders streamed output, Esc cancels.
- Working-dir pick persists per task (reopen pre-fills it); post-to-Comments + launch-location honoured.

## Out of scope

- Quick Updates in single-task mode (#297) — a separate deferral in `SingleTaskApp`.
- Any change to the dashboard's dispatch behaviour or the Dispatch pane UI.
</content>
