# Plan — #94 D2: Dispatch pane one-off vs interactive session toggle (`claude -p` vs `claude`)

Part of the #90 epic. Depends on #93 (Dispatch pane — merged) and #91 (config wiring — merged),
both `CLOSED`.

## Goal / acceptance

Let the user choose, per dispatch, between an **interactive** session (default,
`claude "[prompt]"`) and a **one-off** run (`claude -p "[prompt]"`) that executes non-interactively
and exits. This issue owns **the toggle + mode plumbing + the `-p` command shape**; the richer
one-off execution model (background child process, "thinking" indicator, in-TUI output) is #99's job
and is explicitly out of scope here.

Acceptance signals (from the issue):
- A toggle in the Dispatch pane (#93), reachable via Tab, default = interactive (preserves today's
  behaviour). Seeded from the persisted `AgentDispatchSettings.DefaultSessionMode` (#101).
- The chosen mode threads through the dispatch payload (`DispatchRequest` → `TodoApp.DispatchAgent`
  → `AgentDispatcher.DispatchAsync`) so it reaches the execution path.
- One-off builds the command through the planner as `claude -p …`, still reading the prompt **from
  the temp file** (never inlined into argv). Interactive is byte-for-byte unchanged.
- Interim one-off goes through the existing terminal path (until #99). PowerShell already uses
  `-NoExit` so the window survives; the POSIX/macOS variants must not vanish before the user reads
  the output.
- Command-planner unit tests assert `-p` present/absent per mode across POSIX + PowerShell (+ macOS);
  prompt still read from file; mode plumbed through the dispatch payload.

## Design

`AgentSessionMode` (enum, Interactive/OneOff) already lives in `ClickUpTodo.Configuration`, which
already depends on `ClickUpTodo.Agent`. To avoid a dependency cycle, the **Agent layer stays free of
Configuration** and threads a plain `bool oneOff`; the `AgentSessionMode → bool` mapping happens at
the Tui boundary (`DispatchRequest`/`TodoApp`, which already reference both namespaces).

### Phase 1 — Agent layer: `-p` command shape + `bool oneOff` threading (+ tests)

- `TerminalCommandPlanner.Plan(...)`: add trailing `bool oneOff = false` (optional ⇒ existing call
  sites/tests unchanged). Thread through `PlanWindows/PlanMacOS/PlanLinux` into the command builders.
  - `PwshCommand(file, options, oneOff)`: when `oneOff`, insert `-p` right after the executable
    (`& 'claude' '-p' …extra… (Get-Content -Raw '<file>')`). Windows hosts already launch with
    `-NoExit`, so the window stays open — no keep-alive needed.
  - `PosixCommand(file, options, oneOff)`: when `oneOff`, insert `-p` right after the executable and
    append a keep-alive so the terminal doesn't vanish on exit (Linux `bash -lc`, macOS `do script`):
    `…"$(cat '<file>')"; printf '\n[claude -p finished — press Enter to close] '; read -r _`.
    Interactive omits both (unchanged).
- `ITerminalLauncher.LaunchAsync` / `TerminalLauncher.LaunchAsync`: add `bool oneOff = false`, pass to
  `Plan`.
- `AgentDispatcher.DispatchAsync`: add `bool oneOff = false`, pass to the launcher.
- Tests (`TerminalLauncherTests`, `AgentDispatcherTests`): `-p` present in one-off / absent in
  interactive across pwsh/POSIX/macOS; prompt still file-read; keep-alive present only in one-off
  POSIX; interactive command strings unchanged (existing tests already pin these); `oneOff` plumbed
  through `DispatchAsync` (FakeLauncher captures it).

### Phase 2 — Tui plumbing: payload carries mode, pane toggle goes live, TodoApp threads it

- `DispatchRequest`: add `AgentSessionMode SessionMode = AgentSessionMode.Interactive` (optional ⇒
  the `new DispatchRequest(text)` call site stays valid).
- `TaskDetailScreen`: constructor gains `AgentSessionMode defaultSessionMode = Interactive`; seed the
  `_oneOffToggle` (`CheckBox.Value`); drop the "(coming soon)" label → real wording. `SubmitDispatch`
  reads the toggle → `SessionMode` and includes it in the `DispatchRequest`.
- `TodoApp`: build the screen with `_config.AgentDispatch.DefaultSessionMode`; `DispatchAgent` gains a
  `AgentSessionMode sessionMode` param, maps to `bool oneOff`, and passes `oneOff:` to
  `DispatchAsync`.

TUI is not CI-testable; verified by build + reasoning (and `tui-validate` after `dotnet test` green).
The single sectioned ListView / no-second-focusable-pane rule (#3) is untouched — the toggle is an
existing control inside the already-built Dispatch pane.

## Out of scope / deferred
- #99: one-off as a background child process + thinking indicator + in-TUI output (the interim here
  uses the terminal path with `-p`, per the issue).
</content>
