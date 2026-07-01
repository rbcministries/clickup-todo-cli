# Plan — cmd.exe last-resort terminal fallback (issue #45)

Follow-up to **#25 / PR #42** (S2 of the #23 dispatch epic). PR #42 shipped the
Windows chain **Windows Terminal → pwsh → powershell** and *deliberately omitted*
the `cmd start` last resort the #25 issue text listed, because:

- The pwsh command contains `&` and parentheses
  (`& 'claude' (Get-Content -Raw '<file>')`). Nesting that through
  `cmd /c start "" pwsh -NoExit -Command "…"` is unreliable — `cmd.exe` parses its
  command line by different rules than the `CommandLineToArgvW` escaping that
  `ProcessStartInfo.ArgumentList` applies, so the `&`/parens can be mis-tokenized.
- It couldn't be verified headlessly, and `powershell.exe` ships in-box on every
  supported Windows, so the existing chain always resolves a real terminal — making
  the fallback effectively unreachable and not worth an unverified path at the time.

This issue adds that last resort **robustly**.

## Acceptance criteria (from the issue)

- Add a `cmd`-based last resort that reliably opens a new window when the earlier
  candidates don't apply.
- Verify the `&`/parenthesis payload survives `cmd.exe` + `start` parsing (the issue
  suggests a cmd-safe encoding, a temp `.ps1` launched via `-File`, or
  `UseShellExecute=true` launching pwsh directly).
- Re-introduce the candidate in `TerminalCommandPlanner.PlanWindows` and, if desired,
  a `PreferredTerminal.Cmd` option.

## Design decision — how to make the payload cmd-safe

Chosen approach: **`powershell -EncodedCommand <base64>`**, launched via
`cmd /c start "" …`.

`-EncodedCommand` takes a Base64 string of the **UTF-16LE** bytes of the command.
Base64 is `[A-Za-z0-9+/=]` only — it contains **no** `&`, parentheses, quotes, or
spaces, so it passes through `cmd.exe`'s tokenizer (and `start`) completely intact.
This directly eliminates the exact fragility PR #42 cited, and it is a standard,
well-documented PowerShell mechanism. It is also **fully unit-testable**: decode the
`-EncodedCommand` argument (Base64 → UTF-16LE) and assert it equals the same
`PwshCommand(file, options)` the direct pwsh/powershell candidates run.

Rejected alternatives:
- *Inline the pwsh command in cmd's line* — the original fragile path; rejected.
- *Temp `.ps1` launched via `-File`* — works, but the pure planner is I/O-free and
  must not write files; the composer (#24) owns temp-file writing. Encoding keeps the
  planner pure.
- *`UseShellExecute=true` launching pwsh directly* — changes the launcher's process
  model (the launcher uses `UseShellExecute=false` uniformly) and doesn't address the
  cmd-parsing question the issue asks to verify.

### A PowerShell host is required

`-EncodedCommand` needs a PowerShell host, so the cmd candidate runs
`pwsh` (preferred) or `powershell` inside the `start` window. It is therefore gated on
a PS host being present: `exists("cmd") && (exists("pwsh") || exists("powershell"))`.
This keeps the previous invariant that `cmd` **alone** (no PS host) yields no
candidate — running the file-reading `claude` invocation in bare `cmd.exe` (no
`Get-Content`/`$(cat …)` equivalent, no clean way to read a multi-line file into one
argument) is not reliably possible, so we do not attempt it. In practice
`powershell.exe` is always in-box, so the cmd candidate is available but sits **last**
in the chain — a genuine last resort reached only if the direct pwsh/powershell
launches fail to start, or when the user explicitly prefers cmd.

## Changes

- **`TerminalLauncherOptions.cs`** — add `PreferredTerminal.Cmd` (so a user can pin it;
  forward-compat with #27's settings surface).
- **`TerminalCommandPlanner.cs`**:
  - Add `Cmd` to the default Windows fallback `order` (last).
  - New builder case: gated on `exists("cmd")` **and** a PS host; picks
    `pwsh` if present else `powershell` as the host; emits
    `LaunchSpec("cmd", ["/c", "start", "", <host>, "-NoExit", "-EncodedCommand", <b64>], cwd, "Command Prompt (cmd)")`.
  - New pure helper `EncodePwshCommand(string) → Base64(UTF-16LE)` (mirrors the
    existing quoting helpers; deterministic, no I/O).
  - Update the block comment that currently says the cmd fallback is omitted.

## Tests (`tests/ClickUpTodo.Tests/TerminalLauncherTests.cs`)

Repurpose the now-stale `Windows_CmdIsNotAFallback` and add coverage:

- **`Windows_Cmd_RequiresPowerShellHost`** — `Present("cmd")` alone → **empty** (the
  old invariant still holds: cmd needs a PS host to run the payload).
- **`Windows_Cmd_IsLastResortFallback`** — `Present("wt","pwsh","powershell","cmd")` →
  order ends with `"Command Prompt (cmd)"`; the earlier three are unchanged.
- **`Windows_Cmd_EncodesPwshPayload_SoItSurvivesCmdParsing`** — decode the
  `-EncodedCommand` arg (Base64 → UTF-16LE) and assert it equals the direct pwsh
  candidate's `-Command` string; assert the base64 arg contains none of `& ( ) ' "`.
- **`Windows_Cmd_UsesStartToOpenNewWindow`** — argv begins
  `["/c", "start", "", "pwsh", "-NoExit", "-EncodedCommand"]` (host = pwsh when present).
- **`Windows_Cmd_FallsBackToPowerShellHost_WhenNoPwsh`** — `Present("cmd","powershell")`
  → host arg is `powershell`.
- **`Windows_Cmd_Preferred_PinsToFront`** — `Preferred = Cmd` with `Present("cmd","powershell","wt")`
  → cmd first, fallback preserved.
- **`Windows_Cmd_CarriesWorkingDirectory`** — cwd flows onto the cmd spec (covered by
  the existing `WorkingDirectory_FlowsOntoEverySpec`, extended to include cmd).

All are pure planner tests → run in CI.

## Hard-rule checkpoints

- No `Generated/` edit, no curated-spec change, no Kiota regen — this is pure launcher
  logic on `main`.
- No Terminal.Gui change (no second focusable pane).
- The raw-`Authorization` auth path is untouched.

## Verification

- `dotnet build clickup-todo.slnx -c Release` (0 warn / 0 err),
  `dotnet test clickup-todo.slnx -c Release`, `dotnet format`.
- The real `Process.Start` path can't run headlessly; it stays behind the injected
  `start` delegate. Manual Windows check documented in the PR: on a box where the
  direct pwsh/powershell launch is forced to fail (or with `Preferred = Cmd`), the
  launcher opens a new window via `cmd start` and `claude` receives the file's prompt.
