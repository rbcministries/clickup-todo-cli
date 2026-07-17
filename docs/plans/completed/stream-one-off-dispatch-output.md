# Plan: D6 stretch — stream one-off dispatch output into the TUI as it runs (#187)

Follow-up to #99 (merged, `background-one-off-dispatch.md`). #99 shipped phase 1: `claude -p` runs as a
background child, a spinner shows while it works, and the **final captured output** is rendered when it
completes. This issue delivers the deferred stretch: stream the agent's output **incrementally into the
view as it arrives** instead of one final dump.

All building blocks are on `main`: `Agent/IBackgroundAgentRunner` + `BackgroundAgentRunner`,
`Agent/AgentDispatcher.DispatchBackgroundAsync`, `Tui/Screens/AgentRunScreen` + `AgentRunModel`,
`TodoApp.RunBackgroundDispatch`.

## Goal (issue acceptance criteria)

- Run `claude -p` with `--output-format stream-json` and parse the incremental JSON events.
- Append parsed output to `AgentRunScreen` live (marshalled via `Application.Invoke`), so the user sees
  progress rather than a blank spinner until the very end.
- Keep the seam testable: `IBackgroundAgentRunner` surfaces incremental chunks (an `IProgress<string>`
  callback) so a fake can emit scripted chunks and the assembly/parse logic is unit-tested without a
  real `claude`.
- Single focusable-pane model preserved (no #3/#38 latency regression). Real-`claude` boundary tests
  stay `SkippableFact`.

## Design decisions

- **CLI flags.** `claude -p --output-format stream-json --verbose`. Stream-json print mode *requires*
  `--verbose`, so `BackgroundAgentRunner.BuildArguments` now emits `-p --output-format stream-json
  --verbose` before the configured `ExtraArgs`. Only the background one-off path uses `BuildArguments`;
  the interactive terminal path (`TerminalCommandPlanner`) is untouched.
- **Event stream = JSONL.** stream-json is newline-delimited JSON, one event object per line. A new pure
  `Agent/AgentStreamJson.ParseLine(string)` extracts the human-readable display text from a single line:
  - `type=="assistant"` → each `message.content[]` block: `text` blocks emit their text; `tool_use`
    blocks emit a compact `⚙ {name}` activity marker (so a long tool-running stretch still shows
    progress, not a frozen spinner).
  - `type=="result"` → emit the `result` string **only when `is_error==true`** (surfaces an error
    detail); on success the `result` field just duplicates the final assistant text, so it's ignored.
  - `system`, `user` (tool results — too noisy), blank/invalid lines → nothing. Never throws.
- **Chunk = display line + `"\n"`.** The runner reports each parsed chunk via `progress?.Report(piece)`
  **and** appends the identical `piece` to a `StringBuilder`. So the accumulated `BackgroundRunResult.Output`
  is byte-identical to the concatenation of the streamed chunks. On completion the host still calls
  `AgentRunModel.FormatOutput(run)` (unchanged) — which equals the streamed text on success, plus the
  stderr/exit-code footer on failure — and the screen replaces its live buffer with that authoritative
  render. No divergence, no duplication; the existing `AgentRunModel` surface is untouched.
- **Deadlock-safety preserved.** stdout is now read line-by-line (the main flow) while stdin is fed on a
  **concurrent** task and stderr is drained concurrently (as before), so a child that fills the stdout
  pipe before consuming all of stdin can't deadlock. Cancellation still kills the tree and observes the
  in-flight stdin/stderr tasks.
- **Backward-compatible seam.** `IBackgroundAgentRunner.RunAsync` gains an optional
  `IProgress<string>? progress = null` last-before-`ct` parameter; `AgentDispatcher.DispatchBackgroundAsync`
  threads it through. Callers that don't care pass nothing (buffered result unchanged).

## Phases

1. **Parser + runner seam (pure logic, fully unit-tested).**
   - `Agent/AgentStreamJson.cs` — pure `ParseLine`.
   - `IBackgroundAgentRunner.RunAsync` gains `IProgress<string>? progress`.
   - `BackgroundAgentRunner`: new args, line-loop parse→report→accumulate, concurrent stdin feed.
   - `AgentDispatcher.DispatchBackgroundAsync` threads `progress`.
   - Tests: new `AgentStreamJsonTests` (event shapes, tool markers, error result, malformed lines,
     multi-block, unicode); update the two `BuildArguments` tests; add a dispatcher test asserting
     progress chunks are forwarded and their concatenation equals `run.Output`.
2. **TUI wiring.**
   - `AgentRunScreen.AppendOutput(string)` — live append (clears the "Working…" hint on first chunk,
     follows the tail); completion still routes through `ShowResult`/`ShowCancelled`.
   - `TodoApp.RunBackgroundDispatch` builds an `IProgress<string>` that `Application.Invoke`s
     `AppendOutput`, passed to `DispatchBackgroundAsync`.
   - `dotnet build`/`test`/`format`; then `tui-validate` to confirm no latency/render regression.

## Out of scope / deferred

- Token-level partial-message streaming (`--include-partial-messages`) — per-message granularity is
  enough for a progress view; finer granularity is a possible follow-up.
- Rendering tool **inputs/results** inline (noisy, potentially huge) — only a compact tool-name marker.
- A run queue / concurrency cap — the existing `_dispatching` guard already serialises runs (#99).
