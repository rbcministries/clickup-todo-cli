# Plan — S3: Agent dispatch — detail-view input & wiring (issue #26)

Part of the **#23** epic ("Dispatch an interactive `claude` session from the task
detail view"). This is **S3**: wire the **`A`** keybinding + a modal prompt input
into the detail view, and on submit run the (already-merged) composer (S1 / #24)
→ launcher (S2 / #25). S4 (#27, the settings surface) stays in its own issue.

Dependencies — all on `main`:
- **#17** — `TaskDetailScreen` + `TaskDetail`/`CommentItem` + on-demand fetch.
- **#24** — `AgentPromptComposer.WritePromptFile(task, comments, userPrompt)`.
- **#25** — `ITerminalLauncher.LaunchAsync(promptFilePath, workingDir, options)`.

## Acceptance criteria (from the issue)

- Add the **`A`** keybinding to the detail view; show it in the help/footer.
- Modal prompt input labelled *"Prompt for Claude:"*: `Enter` submits,
  `Shift+Enter` newline (if multi-line), `Esc` cancels.
- On submit: composer (S1) → launcher (S2); show a transient status
  ("Launched Claude for …"); return focus to the detail view.
- The dashboard background refresh continues while the external session runs.

## Design decisions

- **Inline input, not a nested run-loop or a second screen.** The repo pivoted
  away from nested `Application.Run` modals (#38/#41) because they compete with
  the background refresh and feel laggy (the #3 class). The screen seam
  (`TodoApp.ShowScreen`) also supports only **one** active screen, and the
  detail screen is already that screen — so the prompt input is a **transient
  child view inside `TaskDetailScreen`** (a hidden `FrameView` + `TextField`
  shown on `A`). This keeps everything in the single already-open screen and
  makes "return focus to the detail view" natural (just re-focus the pane).
  It is **not** a second focusable pane in the *dashboard* `ListView`, so the #3
  input-latency rule is untouched.
- **Single-line `TextField`, `Enter` submits, `Esc` cancels.** The AC only
  requires Shift+Enter newline *"if multi-line"*; a single-line field satisfies
  it and avoids Terminal.Gui `TextView` Enter-vs-newline ambiguity. An
  empty/whitespace prompt on `Enter` is treated as **cancel** (no accidental
  launch on a stray Enter).
- **The screen raises an event; `TodoApp` owns the launch.** Mirrors the
  existing `OpenBrowserRequested` seam (process launching lives in `TodoApp`,
  not the screen). But dispatch does **not** close the detail view, so it's a
  live `event EventHandler<string> AgentDispatchRequested` (prompt text), not a
  read-after-close flag.
- **`A` is detail-view only.** Bare letters are reserved for the dashboard
  `ListView` type-ahead (#12); the detail panes are read-only `TextView`s with
  no type-ahead, and the issue explicitly asks for `A` here. So `A` is bound in
  the detail screen only; the dashboard footer is unchanged.
- **`workingDir` = null (inherit) for this slice.** Making the working directory
  (and preferred terminal / claude path / args) configurable is S4 (#27);
  `AgentDispatcher` takes `TerminalLauncherOptions` so #27 just populates it.

## New testable seam — `Agent/AgentDispatcher.cs`

Keeps `TodoApp` thin and puts the compose->launch orchestration behind a
unit-testable class (the TUI glue itself isn't CI-testable).

- `AgentDispatcher(ITerminalLauncher launcher, TerminalLauncherOptions? options = null, string? promptDirectory = null)`
- `Task<AgentDispatchResult> DispatchAsync(TaskDetail task, IReadOnlyList<CommentItem> comments, string userPrompt, string? workingDir = null, CancellationToken ct = default)`
  - `AgentPromptComposer.WritePromptFile(...)` -> path (in the injectable
    `promptDirectory` for tests) -> `launcher.LaunchAsync(path, workingDir, options, ct)`.
- `static string FormatStatus(string taskName, LaunchResult result)` — pure
  status-line text: success -> `Launched Claude ({terminal}) for '{name}'.`
  (+ any non-fatal `Note`); failure -> `Could not launch Claude: {error}`.
- `record AgentDispatchResult(bool Success, string StatusMessage, string PromptFilePath)`.

## TUI changes (build-verified only)

- **`TaskDetailScreen`**: add `event EventHandler<string>? AgentDispatchRequested`;
  a hidden prompt `FrameView`/`TextField`; `A` shows it (guarded); the field's
  `Enter` submits (fires the event with non-empty text) / `Esc` cancels, both
  hiding the box and returning focus to the current pane; `Tab` trapped while
  the box is open. Hint line gains `A dispatch to Claude`.
- **`TodoApp`**: a `private readonly AgentDispatcher _agent = new(new TerminalLauncher())`;
  in `OpenDetail`, subscribe to `AgentDispatchRequested` -> flash
  "Launching Claude for '…'…" -> off the UI thread `DispatchAsync` -> flash
  `result.StatusMessage` back on the UI thread (try/catch -> error flash). The
  background refresh keeps running throughout (unchanged).
- **`HelpScreen`**: add a line documenting `A` (dispatch, in the detail view).

## Tests (`tests/ClickUpTodo.Tests/AgentDispatcherTests.cs`)

Pure/logic only (no real process, no Terminal.Gui):
- `DispatchAsync` writes the prompt file (content == `AgentPromptComposer.Compose(...)`)
  and passes that exact path + `workingDir` + `options` to a fake launcher;
  returns success message naming the terminal + task.
- Launcher failure -> `Success == false`, message is the error, file still written.
- `FormatStatus`: success (no note / with note appended) and failure.
- Options pass-through (custom `ClaudeExecutable`); null comments treated as empty.

## Verification

- `dotnet build clickup-todo.slnx -c Release` (0 warn / 0 err),
  `dotnet test clickup-todo.slnx -c Release`, `dotnet format`.
- Terminal.Gui glue is not runnable in CI: manual check — open a task (Enter),
  press `A`, type a prompt, `Enter` -> a new terminal opens running `claude`
  seeded from the temp file; status line shows "Launched Claude (…) for '…'";
  focus returns to the detail view; the dashboard keeps refreshing. `Esc` in the
  box cancels without launching.

## Out of scope (own issue)

- Settings surface (preferred terminal / claude path / args / working dir) wired
  to `AppConfig` + the F2 dialog — **#27** (S4). `TerminalLauncherOptions` +
  `AgentDispatcher`'s `workingDir` param are the seams it will populate.
