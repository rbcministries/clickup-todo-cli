# Cross-platform open-in-browser (#308)

Part of the cross-platform release-readiness epic (#312). Gates re-enabling the
`osx-arm64` / `linux-x64` release artifacts.

## Problem

Two code paths open a URL in the user's browser, both by handing the URL to the
OS shell association via `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })`:

- `TodoApp.LaunchBrowser` (`Tui/TodoApp.cs:1452`) — `Enter` opens the focused
  task; failures are caught and flashed.
- `SystemBrowserLauncher.TryOpen` (`Setup/IBrowserLauncher.cs:24`) — the OAuth
  authorize URL during first-run setup.

On Windows this works (shell association → default browser). On macOS/Linux
`UseShellExecute = true` relies on the runtime shelling out to `open` /
`xdg-open` under the hood, and throws (`Win32Exception`) when the opener isn't
found — with no actionable message and no explicit control over which opener is
used. Distributing macOS/Linux binaries needs an explicit, testable path.

## Approach

Mirror the existing cross-platform `TerminalLauncher` / `TerminalCommandPlanner`
design (`Agent/`): a **pure, I/O-free planner** decides the ordered list of
launch candidates from the OS + a PATH probe + env; a thin launcher starts each
until one succeeds. This keeps all the per-platform decision logic unit-testable
without spawning a process (the real `Process.Start` path can't run headlessly,
exactly as the terminal launcher documents).

### `BrowserLaunchPlanner` (pure)

`Plan(OSPlatformKind os, Func<string,bool> exists, Func<string,string?> getEnv, Uri url)`
→ ordered `IReadOnlyList<BrowserCommand>`, already filtered to openers present on
PATH.

`BrowserCommand(string FileName, IReadOnlyList<string> Arguments, bool UseShellExecute, string DisplayName)`
— argument vectors (never a concatenated shell string), matching the repo's
launch convention so the URL can never be interpreted as a shell token.

Per-OS strategy:

- **Windows** — shell-execute the URL (`FileName = url`, `UseShellExecute = true`).
  Unchanged from today; the shell association is the right Windows path.
- **macOS** — `open <url>` (always present on macOS); shell-execute kept as a
  trailing fallback.
- **Linux** — probe PATH in order and emit a spec for each present opener:
  `$BROWSER` (if set and on PATH), `xdg-open`, `gio open`, `x-www-browser`,
  `www-browser`, `sensible-browser`. `xdg-open <url>` is the primary. If none is
  present (headless/minimal), return **empty** so the caller shows an actionable
  message rather than throwing.
- **Unknown** — best-effort shell-execute, preserving old behaviour.

`OpenerHint(OSPlatformKind)` returns a short install hint (Linux → install
`xdg-utils` for `xdg-open`) for the failure message, else `null`.

Reuses `ClickUpTodo.Agent.OSPlatformKind` (the existing OS-family enum the
terminal launcher already uses) and a `CurrentOS()` resolver mirroring
`TerminalLauncher.CurrentOS()`.

### `SystemBrowserLauncher` (real launcher)

Rewritten to run the planner and start each `BrowserCommand` until one launches
(shell-execute vs argv per the spec's `UseShellExecute`), catching the same
narrow exception set as `TerminalLauncher.StartProcess`. All external
dependencies (OS, PATH probe, env, process start) injectable for tests; defaults
resolve the real runtime. `IBrowserLauncher.TryOpen(Uri) : bool` is unchanged so
`OAuthSignIn` / `SetupWizard` keep working — now cross-platform-correct.

### Wiring `TodoApp`

`TodoApp` gains a `private readonly IBrowserLauncher _browser = new SystemBrowserLauncher();`
field (no constructor churn — the TUI isn't unit-tested; the logic under test is
the planner). `LaunchBrowser` routes through `_browser.TryOpen(uri)` and, on
failure, flashes a message that includes the opener hint and the URL so the user
can copy it. TUI redraw on return is inherent — Terminal.Gui repaints after the
child process is spawned; the launch is fire-and-forget and does not block or
take over the terminal (the browser opens as a detached GUI process).

## Testing

`BrowserLaunchTests.cs`, mirroring `TerminalLauncherTests`:

- Planner: Windows → shell-execute; macOS → `open` first; Linux ordering &
  PATH-filtering (xdg-open present/absent, `$BROWSER` honoured, `gio` fallback,
  none-present → empty); Unknown → shell-execute; URL lands in the argument
  vector verbatim.
- Launcher orchestration: tries specs in order, stops at first that starts,
  returns `false` when none start / none planned — all with injected fakes, no
  real process.

`dotnet build` (0 warnings) + `dotnet test` green. TUI `Enter`-to-open is
verified by build + reasoning (can't run headlessly); the planner carries the
behaviour and is fully covered. On this Linux host the `xdg-open` selection path
is confirmed by the unit tests exercising the real PATH probe.

## Out of scope / deferred

- In-text link opening in Task Detail (#318) is a separate issue; it will reuse
  this launcher when it lands.
- Actual browser-open smoke test on real macOS/Linux hosts is part of the
  first-run smoke test (#311).
