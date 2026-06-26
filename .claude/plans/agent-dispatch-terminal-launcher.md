# Plan — S2: Cross-platform terminal launcher (issue #25)

Part of the **#23** epic ("Dispatch an interactive `claude` session from the task
detail view"). This is **S2** only: the launcher that opens a new terminal
tab/window running an interactive `claude` seeded from a temp **prompt file**.
S1 (#24, the composer that writes the file), S3 (#26, the `A`-keybinding +
detail-view wiring) and S4 (#27, the settings surface) stay in their own issues.

The epic says S2 "can proceed in parallel with S1" and the launcher consumes a
**file path**, so this slice depends on nothing else (not #17 / PR #33).

## Acceptance criteria (from the issue)

- `ITerminalLauncher.LaunchAsync(promptFilePath, workingDir, config)` with
  platform implementations selected via `RuntimeInformation.IsOSPlatform`.
- **Windows (primary):** prefer Windows Terminal —
  `wt.exe new-tab pwsh -NoExit -Command "claude (Get-Content -Raw '<file>')"`.
  Fallback chain when `wt` is absent: `pwsh` → `powershell` → `cmd start`,
  each launching the same. `-Raw` feeds the multi-line JSON as one argument.
- **macOS:** `osascript` driving Terminal to run `claude "$(cat '<file>')"`.
- **Linux:** honor `$TERMINAL`, else probe `x-terminal-emulator` →
  `gnome-terminal` → `konsole`, running `claude "$(cat '<file>')"`.
- Build argv **as arrays**, never by concatenating the prompt into a shell
  string — the **file path** (not the prompt content) is what enters the
  command; the file indirection is what makes this safe.
- Surface launch failures so the caller can show them in the TUI status line
  (e.g. "no terminal found" / "claude not on PATH").
- Mirrors the existing cross-platform `Process.Start` used for open-in-browser
  in `TodoApp`, with the per-platform terminal strategy above.

## Design

New folder `src/ClickUpTodo/Agent/`, namespace `ClickUpTodo.Agent`.

### Pure, testable core — `TerminalCommandPlanner` (no I/O)

`static IReadOnlyList<LaunchSpec> Plan(OSPlatformKind os, Func<string,bool> exists,
Func<string,string?> getEnv, string promptFilePath, string? workingDir,
TerminalLauncherOptions options)`

- Returns the **ordered** list of candidate processes to try, already filtered
  to those whose executable is present (`exists`) — so the launcher just tries
  each in order until one starts.
- The `claude` invocation is built from `options.ClaudeExecutable` +
  `options.ExtraArgs`, reading the prompt **from the file**:
  - Windows (pwsh): `<claude> <args> (Get-Content -Raw '<file>')`
  - POSIX (bash): `<claude> <args> "$(cat '<file>')"`
- `workingDir` is carried on `LaunchSpec.WorkingDirectory` (applied to
  `ProcessStartInfo.WorkingDirectory`) — never `cd`'d into the command string.
- Quote-escaping is centralized and unit-tested: PowerShell single-quote → `''`;
  POSIX single-quote → `'\''`.
- `options.Preferred` (Auto by default) lets a specific Windows terminal be
  pinned to the front (forward-compat hook for #27); Auto = the full fallback
  chain.

### Records / enums

- `OSPlatformKind { Windows, MacOS, Linux, Unknown }`
- `LaunchSpec(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory, string DisplayName)`
- `LaunchResult` — `Success`, `LaunchedWith`, `Error` (+ `Ok`/`Fail` factories).
- `TerminalLauncherOptions` — `ClaudeExecutable` ("claude"), `ExtraArgs` ([]),
  `Preferred` (Auto). Intentionally lean; #27 extends + wires to `AppConfig`/F2.
- `PreferredTerminal { Auto, WindowsTerminal, Pwsh, PowerShell, Cmd }`.

### Thin shell — `TerminalLauncher : ITerminalLauncher`

- Real `exists` = scan `PATH` (+ `.exe`/`.cmd`/`.bat` on Windows); real
  `getEnv` = `Environment.GetEnvironmentVariable`; OS = `RuntimeInformation`.
- `LaunchAsync`: guard prompt file exists → `Plan(...)` → if empty,
  `Fail("No terminal emulator found …")` → try `Process.Start` on each spec
  (via an injected `start` delegate) until one succeeds → `Ok(displayName)`;
  all fail → `Fail`.
- Dependencies (`exists`, `getEnv`, `start`, `fileExists`, `os`) are injectable
  so the orchestration loop is unit-testable without spawning a real process.

## Tests (`tests/ClickUpTodo.Tests/TerminalLauncherTests.cs`)

Planner (pure, all in CI):
- Windows ordering wt → pwsh → powershell → cmd; only present exes included;
  `wt` argv = `new-tab pwsh -NoExit -Command <cmd>`; pwsh argv = `-NoExit -Command <cmd>`.
- macOS: `osascript -e` AppleScript that runs `claude "$(cat '<file>')"`.
- Linux: `$TERMINAL` honored first; else `x-terminal-emulator`/`gnome-terminal`/
  `konsole` in order; inner command references `cat '<file>'`.
- The prompt **content** never appears inline — only the **file path** does
  (assert command references `Get-Content`/`cat` + the path).
- Escaping: a path with a single quote is escaped per shell.
- `WorkingDirectory` propagates; `ExtraArgs` and a custom `ClaudeExecutable`
  appear; `Preferred` pins/filters the Windows candidate.

Launcher orchestration (injected `start`/`exists`, no real process):
- First candidate fails to start → second is tried → `Ok` with its name.
- No candidates present → `Fail` ("no terminal found").
- Missing prompt file → `Fail`.

## Out of scope (own issues)

- The prompt composer / temp-file writer — **#24** (S1).
- `A` keybinding + detail-view input + status-line display — **#26** (S3).
- Settings surface (preferred terminal / claude path / args / cwd) wired to
  `AppConfig` + the F2 dialog — **#27** (S4). `TerminalLauncherOptions` is the
  seam it will populate.

## Verification

- `dotnet build -c Release` (0 warn / 0 err), `dotnet test -c Release`,
  `dotnet format`.
- The real `Process.Start` path can't run headlessly in CI; it's isolated
  behind the injected `start` delegate and exercised manually (documented in
  the PR). All command-construction + fallback logic is covered by unit tests.
