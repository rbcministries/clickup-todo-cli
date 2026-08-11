# Dispatch (Windows): escape the `wt` subcommand delimiter (`;`)

Issue: #534. Bug — broken since #252 (`867f5f9`).

## Problem

`TerminalCommandPlanner` builds one pwsh command string and hands it to `wt` as a
single argv element (`WtArgs`, `TerminalCommandPlanner.cs:247`). When a dispatch
resolves a non-blank working directory, `PwshCommand` (`:415`) prepends a
`Set-Location -LiteralPath '<dir>' -ErrorAction Stop; <command>` guard — introducing
a `;`.

`;` is **Windows Terminal's own subcommand delimiter**, and `wt` splits on it *inside*
arguments; quoting does not protect it (WT's documented escape is `\;`). So a dispatch
with a working directory through Windows Terminal opens **two** tabs:

1. `pwsh` in the right directory but with no `claude` (the part before the `;`).
2. The default profile trying to launch the literal text after the `;`
   (`" & 'claude' (Get-Content -Raw '…')"`) as an executable → `0x80070002`
   (`ERROR_FILE_NOT_FOUND`).

`wt` exits 0 regardless, so the dispatch falsely reports success.

**Blast radius:** every Windows dispatch that resolves a non-blank working directory and
launches through Windows Terminal — an explicit pane pick (#95), a cached per-task dir
(#96), the task-derived base dir (#98), `Fixed`/`Home` modes, a #461 `Repository` match —
both the `new-tab` (new window) and `-w 0 new-tab` (current window) forms. A blank
working directory is unaffected (no `;`), which is why it survived.

## Fix

**Escape at the WT boundary.** `WtArgs` is the single choke point where every `wt`
argument is assembled (both forms, with or without a #462 `-p <profile>`). Replace `;`
with `\;` (WT's documented escape, which WT unescapes before handing the commandline to
the profile) on **every** argument it emits. This covers the `Set-Location` `;` and any
other `;` that could reach `wt` — one embedded in a configured `ClaudeExecutable` path,
in an `ExtraArgs` entry, or in a matched #462 profile name.

Applying the escape to every emitted arg is safe: the structural WT tokens
(`new-tab`, `-w`, `0`, `-p`, `pwsh`, `-NoExit`, `-Command`) contain no `;`, so the
replacement is a no-op on them; we never emit a `;` as a *structural* WT delimiter
ourselves (subcommands are separate argv elements), so every `;` that appears is data
that must survive to the profile intact.

Backslashes in Windows paths are **not** touched — WT does not treat `\` as a general
escape elsewhere (tab 1 already sat in the correct `C:\…` directory today), so only `;`
needs escaping.

The `-ErrorAction Stop` guard that makes a failed directory change terminating is kept.

### Not chosen (documented alternative)

Passing the directory natively as `wt new-tab -d <dir>` and dropping the `Set-Location`
prefix for WT specs only. It removes the `;` at source for the common case and is the
idiomatic WT form, but it requires threading `cwd` separately for just the WT candidate
(today one command string is shared across all Windows candidates), changes the
missing-directory failure mode from "pwsh aborts with a visible error" to "WT fails to
open the tab", and needs a check against #462's `-p` interaction with `-d`. The escape
fix is smaller, keeps the shared command string, and also covers the profile-name /
executable-path / extra-arg semicolons the `-d` approach would not. Left for a follow-up
if the idiomatic form is ever wanted.

## Tests (`TerminalLauncherTests` / `TerminalCommandPlannerWtProfileTests`)

Correctness updates to existing wt-path assertions (they asserted the *unescaped* `;`,
i.e. the buggy output):

- `Windows_WindowsTerminal_BakesSetLocationIntoTheTab` — now expects `…Stop\;`.
- `NewTab_BakesWorkingDirectoryIntoTheTabCommand` (wt leg) — now expects `…Stop\;`.

New coverage per the acceptance criteria:

- The `wt` argv for a dispatch with a working directory contains **no unescaped `;`** —
  `new-tab` and `-w 0 new-tab` forms, and with a #462 `-p` present.
- A `;` in `ClaudeExecutable`, in an `ExtraArgs` entry, and in a matched profile name is
  escaped in the `wt` argv.
- `pwsh` / `powershell` specs are unchanged; the `cmd` `-EncodedCommand` base64 still
  decodes to the literal (unescaped) `;` command — no escaping leaks into paths that
  don't re-parse.
- POSIX/macOS specs and the `PlanAppLaunch` (#301) argv are byte-identical to today.
- A blank working directory ⇒ byte-identical to today on every platform.

## Manual verification (not CI-testable)

On Windows Terminal, from inside WT: `Ctrl+A` → working dir `~/source/repos/ODBM.Secure`
→ new tab → exactly **one** tab, in that directory, with `claude` running. No second tab,
no `0x80070002`.
