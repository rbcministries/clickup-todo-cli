# Plan: D6 — one-off dispatch runs in the background with a thinking indicator, output in the TUI (#99)

Part of epic #90. Blockers #91 (config wiring), #93 (Dispatch pane), #94 (one-off toggle) and the
shared-path issue #97 (post-to-Comments) are all **closed/merged**, so the
`DispatchRequest → AgentDispatcher.DispatchAsync → AgentPromptComposer` path is stable.

## Goal (issue acceptance criteria)

When the Dispatch pane's **one-off** mode is chosen (`DispatchRequest.SessionMode == OneOff`, #94),
do **not** open a visible terminal window. Instead:

1. Run `claude -p` as a **background child process of the app** (redirected stdout/stderr), off the UI
   thread, marshalling UI updates via `Application.Invoke` (mirrors the existing `_dispatching` guard +
   off-thread pattern in `TodoApp.DispatchAgent`).
2. Show an animating **"thinking" indicator** (spinner + label) in the TUI while it runs, driven by
   `Application.AddTimeout`. Non-blocking: the background dashboard refresh keeps running.
3. Allow **Esc to cancel** the in-flight run (kill the child process), surfaced in the UI.
4. On completion, **render the captured output** in a scrollable, read-only `TextView` (consistent with
   the detail screen), with a non-zero exit / stderr surfaced as an error state.

**Interactive** mode is unchanged — it still opens a real terminal via the launcher (#25), because an
interactive session needs a live TTY. The interactive dispatch path stays byte-for-byte identical.

## Scope for this session (issue's suggested phasing)

Ship **phase 1**: spinner while running → render the **final captured output** when the run completes,
plus Esc-to-cancel. The **streaming** stretch (incremental `--output-format stream-json` parsing into
the view as lines arrive) is deferred to a dedicated follow-up issue, linked from the PR.

**Output format decision:** phase 1 consumes `claude -p`'s **plain-text** stdout (its default). Plain
text is the simplest correct choice for a final-output dump; `stream-json` only earns its keep once we
parse incrementally, which is the deferred streaming stretch. Documented here and in the PR.

**Concurrency decision:** one background run at a time. The existing UI-thread `_dispatching` re-entrancy
guard already prevents a duplicate submit, and while the run screen is mounted the (hidden) detail
screen receives no keys, so a second dispatch can't start. Documented; a queue/cap is out of scope.

**Prompt-file cleanup:** the background path reads the composed prompt file itself (feeds it to the
child's stdin) and then deletes it once the process exits — unlike the interactive path, which retains
the file for the launched terminal to read.

## Design / seams

### Backend (unit-testable, no real process)

- `Agent/IBackgroundAgentRunner.cs` — seam:
  `Task<BackgroundRunResult> RunAsync(string promptFilePath, string? workingDir, TerminalLauncherOptions options, CancellationToken ct)`.
  A `BackgroundRunResult(bool Started, int? ExitCode, string Output, string? Error)` with a `Success`
  helper (`Started && ExitCode == 0`). Tests inject a fake that returns scripted output/exit code and
  can observe cancellation — no real `claude` needed (mirrors `FakeLauncher` in `AgentDispatcherTests`).
- `Agent/BackgroundAgentRunner.cs` — default impl over `System.Diagnostics.Process`: `claude -p`
  (+ configured extra args), redirected stdin/stdout/stderr, `UseShellExecute=false`, working dir
  applied. Reads the prompt file and writes it to the child's **stdin** (arg-length-safe for large
  prompts, unlike a positional arg). Captures stdout + stderr; `WaitForExitAsync(ct)`; on cancel, kills
  the process tree and throws `OperationCanceledException`. Missing-exe (`Win32Exception`) →
  `Started=false` with a helpful message. Argument construction factored into a pure
  `BuildArguments(options)` for a unit test.
- `Agent/AgentDispatcher.cs` — add `DispatchBackgroundAsync(...)` mirroring `DispatchAsync`'s params
  (prompt/workingDir/template/outputSubdirectory/postToComments/ct) but routing to the background runner
  instead of the terminal launcher, and deleting the prompt file in a `finally`. Ctor gains an optional
  injected `IBackgroundAgentRunner` (defaults to `new BackgroundAgentRunner()`); the existing
  `ITerminalLauncher` ctor arg and interactive path are untouched.

### UI model (unit-testable)

- `Tui/Screens/AgentRunModel.cs` — pure spinner + state machine: braille spinner frames, `Advance()`
  returns the next running header (`"⠹ Claude is working on '<task>'…"`), phase transitions
  (`Running → Succeeded | Failed | Cancelled`), and the completed header per phase. No Terminal.Gui
  types, so it is fully unit-tested (matching the repo's pure-surface screen-testing pattern).
- `HelpItemSets.AgentRun` — footer set for the run screen (Esc cancel/back, F1 help).

### TUI glue (build + reasoning; not CI-testable)

- `Tui/Screens/AgentRunScreen.cs` — full-window `Screen`: a header `Label` (spinner/result line) over a
  read-only, word-wrapped `TextView` for the output. Owns the `Application.AddTimeout` spinner while
  running (removed on completion/dispose). `KeyDown`: Esc raises `CancelRequested` while running, and
  closes the screen once the run has completed/failed/cancelled; F1 → help. Host calls
  `ShowResult(output, phase)` (via `Application.Invoke`) to stop the spinner and switch to the output.
- `Tui/TodoApp.cs` — in `DispatchAgent`, branch on `request.SessionMode`: interactive keeps today's
  terminal path exactly; one-off resolves the same dispatch settings (working dir #91/#95/#96/#98,
  template #100, subdir #98, post-to-Comments #97 — shared with the interactive path so behaviour is
  identical), mounts an `AgentRunScreen` over the detail screen through the existing `ShowScreen` seam,
  and runs `agent.DispatchBackgroundAsync` off-thread with a `CancellationTokenSource` wired to the
  screen's `CancelRequested`. Completion/failure/cancel marshal back via `Application.Invoke` to
  `screen.ShowResult`.

## Phases (commit + push each; first push opens the draft PR)

1. **Backend seam + tests** — `IBackgroundAgentRunner`, `BackgroundAgentRunner`,
   `AgentDispatcher.DispatchBackgroundAsync`; `AgentDispatcherTests` background cases (fake runner:
   forwards prompt/workingDir/options, deletes the prompt file, surfaces exit code, cancellation) and a
   `BackgroundAgentRunner.BuildArguments` unit test.
2. **Pure UI model + tests** — `AgentRunModel` (spinner frames advance/wrap, per-phase headers) and its
   tests; `HelpItemSets.AgentRun`.
3. **TUI screen + wiring + docs** — `AgentRunScreen`, `TodoApp` one-off routing, help footer/`HelpScreen`
   copy, README note. Verify by `dotnet build` + reasoning (TUI not CI-testable); describe manual
   verification in the PR.

## Test plan

- `dotnet build -c Release` (0 warnings/0 errors) and `dotnet test -c Release` green (integration tests
  skip without `CLICKUP_TOKEN`).
- New unit tests: dispatcher background forwarding + prompt-file cleanup + exit-code/cancel handling;
  `BuildArguments`; `AgentRunModel` spinner + phase headers.
- TUI: build + reasoning per the repo's Terminal.Gui rule; manual-verification notes in the PR (single
  focusable pane preserved — no #3 regression).

## Deferred (follow-up issue, linked from PR)

- Streaming: incremental `--output-format stream-json` parsing rendered line-by-line into the view as
  the agent works, instead of a single final dump.
