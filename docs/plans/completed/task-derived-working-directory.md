# T1 — Task-derived working directory (#98, part of epic #90)

## Goal

Give `AgentWorkingDirectory.TaskDerived` (the default mode) real behaviour. Today
`ResolveWorkingDirectory` is called with `taskDerivedDirectory: null`
(`TodoApp.cs:854`), so a task-derived dispatch inherits an arbitrary cwd and the mode is
effectively dead. #98 activates it:

- **Launch in the saved base working directory** (#92 / `AppConfig.DefaultWorkingDirectory`,
  resolved via `SettingsForm.ResolveDefaultWorkingDirectory`) instead of inheriting the cwd.
- **Instruct the agent** (in the composed prompt) to write outputs to a per-task subdirectory
  `./[custom-id]` (falling back to the task id) so each task's work stays separated inside the
  shared base dir.
- **Create the base dir on first task-derived launch** — the #92 wizard deliberately deferred
  directory creation to #98 (its plan: "the dir is created on first task-derived launch (#98)").

Dependencies #91 (config wiring, merged #133) and #92 (base dir, merged #134) are both landed.

## Design decisions

- **Fallback-path semantics.** #98 is the *fallback* when the user makes no explicit working-dir
  pick (#D3/#95) and there's no per-task cache hit (#D4/#96). Those override paths aren't built
  yet, so today: mode `TaskDerived` ⇒ base dir + subdir instruction; `Home`/`Fixed` ⇒ their own
  resolved dir with **no** subdir instruction (the user chose a specific dir). When #95/#96 land
  they resolve to a concrete dir that wins over the task-derived candidate.
- **Where the base dir comes from.** `SettingsForm.ResolveDefaultWorkingDirectory` is the single
  source of truth (#92) — it never returns blank (falls back to `~/ClickUp-Tasks`). Pass its
  result as the `taskDerivedDirectory` candidate; `ResolveWorkingDirectory` already picks it only
  in `TaskDerived` mode, so `Home`/`Fixed` are unaffected.
- **Subdir token.** custom id when set (`TaskDetail.CustomId`), else the task id
  (`TaskDetail.Id`), reduced to a filesystem-safe token by reusing the composer's existing
  `SafeToken` sanitiser (so `../`, separators, spaces can't escape the base dir). `TaskDetail` is
  always in hand at dispatch (the detail view is a prerequisite), so both ids are available.
- **Prompt threading.** Add an optional `outputSubdirectory` parameter to
  `AgentPromptComposer.Compose`/`WritePromptFile` and `AgentDispatcher.DispatchAsync`. A blank
  value emits the prompt exactly as today (zero-config output stays **byte-identical**); a
  non-blank value inserts one instruction line between the user prompt and the preamble.
- **Directory creation, not the subdir.** The app creates the *base* dir (the launch cwd must
  exist or `Process.Start` fails); the *subdir* is left to the agent ("create it if needed" in the
  instruction) to avoid a side effect the agent may not use. Creation is scoped to task-derived
  mode (`Home` always exists; `Fixed` is the user's own external dir, per #133).

## Phase 1 — composer + dispatcher + unit tests (the pure, tested core)

- `AgentPromptComposer`:
  - `public static string OutputSubdirectoryToken(TaskDetail task)` — custom-id → id fallback,
    sanitised via the existing `SafeToken`.
  - `Compose(..., string? outputSubdirectory = null)` and
    `WritePromptFile(..., string? outputSubdirectory = null)` — when non-blank, insert
    `Write any output files to the subdirectory ./{token} (create it if needed).` as its own
    paragraph after the user prompt (before the preamble). Blank ⇒ unchanged layout.
- `AgentDispatcher.DispatchAsync(..., string? outputSubdirectory = null, ...)` — thread through to
  `WritePromptFile`.
- Tests (`AgentPromptComposerTests`, `AgentDispatcherTests`):
  - token: custom-id present → sanitised custom-id; blank/null custom-id → task id; unsafe chars
    sanitised; blank both → `task` fallback.
  - compose: instruction line inserted between prompt and preamble; trimmed; empty prompt ⇒
    instruction leads; **blank/null subdir ⇒ byte-identical to the no-arg compose** (theory).
  - dispatcher: `outputSubdirectory` reaches the composed file; blank ⇒ default; a test mirroring
    the TodoApp task-derived glue (base-dir candidate → workingDir, token → instruction).
  - Existing `Dispatcher_BuiltFromDefaultSettings_MatchesZeroConfigBehaviour` (null candidate ⇒
    null workingDir) stays valid and unchanged — it documents the dispatcher seam, not the glue.

## Phase 2 — TodoApp wiring (build-verified TUI glue)

- `TodoApp.DispatchAgent`:
  - resolve `baseDir = SettingsForm.ResolveDefaultWorkingDirectory(_config.DefaultWorkingDirectory, home)`.
  - `workingDir = settings.ResolveWorkingDirectory(taskDerivedDirectory: baseDir, homeDirectory: home)`.
  - `isTaskDerived = settings.WorkingDirectory == AgentWorkingDirectory.TaskDerived`;
    `outputSubdir = isTaskDerived ? AgentPromptComposer.OutputSubdirectoryToken(detail) : null`.
  - in the background `Task.Run`, before dispatch: when task-derived, `Directory.CreateDirectory`
    the base dir (guarded by the existing try/catch so a creation failure degrades to a clean
    status message).
  - pass `outputSubdir` into `DispatchAsync`; update the call-site doc comment (drop the
    "candidate is null until #98" note).
- No new focusable pane, no keybinding change — the single sectioned `ListView` (#3) is untouched;
  this only changes what directory/prompt a dispatch uses.

## Verification

- `dotnet build clickup-todo.slnx -c Release` (0/0) + `dotnet test -c Release` (green; integration
  skipped w/o `CLICKUP_TOKEN`). `dotnet format` clean.
- TUI glue verified by build + reasoning (per the repo rule). Manual check in the PR: with the
  default (TaskDerived) mode, dispatch from a task's detail view (`Ctrl+A`) — the new terminal
  starts in `~/ClickUp-Tasks` (created if missing) and the seeded prompt contains the
  `./[custom-id]` output instruction; in Home/Fixed mode the dir resolves as before with no
  instruction.

## Deferred (owned by other issues)

- Explicit working-dir pick / file-tree browser that overrides the task-derived candidate — #95 (D3).
- Per-task cached working directory that overrides it — #96 (D4).
