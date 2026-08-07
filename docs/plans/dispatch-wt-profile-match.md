# Dispatch: "Try to use WT profiles" — launch via the matching Windows Terminal profile (#462)

Issue: [#462]. Stacks on **#461** (repo-matched sub-directory, now merged) which makes the
resolved dispatch directory a real per-project path, so matching a per-project WT profile is
meaningful. Soft-sequenced after **#438** (planner entry-point unification, merged).

## Goal

A new F2 **Dispatch** toggle, *"Try to use WT profiles"* (off by default). When on, on Windows
Terminal, a dispatch looks for the first configured profile whose normalised `startingDirectory`
equals the directory Dispatch resolved for the task, and launches **that profile** (`wt … -p
<profile> …`), substituting Dispatch's own command for the profile's — so the session inherits the
profile's font / colour scheme / tab title / environment while still running our composed prompt.

A strict no-op when the toggle is off, when not on Windows, when no `settings.json` is found or it's
malformed, or when no profile matches — byte-identical launch behaviour to today in every such case.

## ⚠ Phase-0 gate (cannot be exercised in CI)

The issue gates the feature on a manual Windows-Terminal spike: does `wt new-tab -p "<name>"
<commandline>` run **our** command rather than the profile's own commandline? This needs a real WT
box and **cannot** run under the `tui-validate` PTY harness (Linux/termios) — the acceptance criteria
say as much ("the `wt` launch itself cannot be covered by the PTY harness … verify that leg manually
on Windows").

**Decision for this unattended run:** deliver the whole CI-verifiable slice, off by default so it
cannot regress anything, and flag Phase 0 as pending manual Windows confirmation in the PR. The
documented strong evidence (the app already uses the trailing-commandline half of `wt new-tab pwsh
-NoExit -Command <cmd>`, `TerminalCommandPlanner.cs:213-217`; `-p <profile> <commandline>` is
documented WT behaviour where the profile contributes appearance/env/startingDirectory and the
trailing commandline overrides) is why building off-by-default is low-risk. If Phase 0 turns out
negative the pure parser/matcher survives for the issue's own fallback (read the profile only to
apply `--title`/`--tabColor`).

Deferred, tracked in the PR: the split-pane `sp -p` data point (#516/#502) and the `-d` vs baked
`Set-Location` redundancy question — both are manual-Windows observations, not code here.

## Design — mirrors the #461 seam exactly

The match is computed **per dispatch** (it depends on the runtime resolved working directory), so it
rides the same `DispatchCoordinator.Plan` → `ResolvedDispatch` → `RunInteractive` path #461 uses for
the repo-dir match, and is surfaced with the same kind of status-line note.

### 1. Pure matcher — `Agent/WindowsTerminalProfileMatcher.cs`

`static string? Match(string settingsJson, string targetDirectory, Func<string,string> expandEnv)`:

- Parse **JSONC** (`JsonDocumentOptions { CommentHandling = Skip, AllowTrailingCommas = true }`) — WT
  ships `//` comments and trailing commas; stock `System.Text.Json` throws without these.
- Read `profiles.list` (object form) **or** a bare `profiles` array (older form). `profiles.defaults`
  is **never** a candidate (its inherited `startingDirectory` would match everything).
- Per profile in **settings.json order** (first match wins, deterministic, documented):
  - skip `"hidden": true`;
  - `startingDirectory` absent/`null`/blank ⇒ inherit ⇒ **never a match** (skip);
  - normalise both sides and compare **case-insensitively** (Windows-only feature, so correct here):
    `expandEnv` (`%USERPROFILE%…`) → normalise `/`→`\` → trim trailing separators.
- Return the profile's **`guid`** when present (stable, unique), else its **`name`** — the value
  passed to `wt -p`.
- Any parse/shape error ⇒ `null` (a broken `settings.json` must never fail a dispatch).

### 2. Locator — `Agent/WindowsTerminalSettings.cs`

- `static IReadOnlyList<string> CandidatePaths(Func<string,string?> getEnv)` — pure, from
  `%LOCALAPPDATA%`: Store stable / Preview / Canary package `LocalState\settings.json`, then the
  unpackaged `%LOCALAPPDATA%\Microsoft\Windows Terminal\settings.json`. First that exists wins.
- `static string? Load(getEnv, fileExists, readAllText)` — first existing candidate's content, else
  `null`. Thin, injectable I/O (unit-tested with fakes). On macOS/Linux `LOCALAPPDATA` is unset ⇒ no
  candidates ⇒ `null`, so it's naturally inert off Windows.

### 3. Thread the matched profile through the launch

- `TerminalLauncherOptions.WindowsTerminalProfile` (`string?`, default `null`).
- `TerminalCommandPlanner.PlanWindows` — the two `wt` specs gain `-p <profile>` right after the
  `new-tab` subcommand when the option is non-blank, for **both** the new-window (`new-tab …`) and
  new-tab (`-w 0 new-tab …`) forms, via a small `WtArgs` helper. Null/blank ⇒ **byte-identical** to
  today. `PlanAppLaunch` passes `options with { WindowsTerminalProfile = null }` so the "open this app
  in a tab" gesture (#301, no working dir) can never emit `-p`, even if a caller set it.

### 4. Per-dispatch wiring (mirrors #461)

- `AgentDispatchSettings.TryUseWindowsTerminalProfiles` (`bool`, default `false`) added to
  `IsDefault`. It does **not** flow through `ToLauncherOptions()` — the profile is per-dispatch, not a
  static option. Absent in an old `config.json` ⇒ off; no migration.
- `DispatchCoordinator.Plan` gains injected `loadWindowsTerminalSettings` / `expandEnvironment`
  delegates (default: real filesystem + `Environment.ExpandEnvironmentVariables`), mirroring #461's
  `directoryExists` / `childDirectoryNames`. When the toggle is on, the dispatch is **interactive**
  (not one-off — a one-off has no terminal), and a `settings.json` loads, it matches `plan.WorkingDir`
  against it and stores the profile on `ResolvedDispatch.WindowsTerminalProfile`. Every other path
  leaves it `null` and reads nothing.
- `AgentDispatcher.DispatchAsync` gains `string? windowsTerminalProfile`, applied to the per-launch
  options the same way `launchLocation` is (`_options with { … }`).
- `DispatchCoordinator.RunInteractive` passes `plan.WindowsTerminalProfile` and appends
  `WindowsTerminalProfileNote(plan)` to the status line (same reasoning as #461's `RepositoryMatchNote`
  — a silent change in how the session launches is unexplainable to the user).

### 5. F2 toggle — `Tui/Screens/SettingsScreen.cs`

An On/Off cycle button in the Dispatch column (mirrors the launch-location / defaults buttons), wired
into the saved `AgentDispatchSettings`. Inert wording, not hidden, on macOS/Linux (follows the
`PreferredTerminal`/launch-location precedent).

## Interactions (per the issue)

- **#385 `CustomTerminalCommand`** is tried ahead of the built-in chain and is a whole different
  emulator; it wins when both are set (it names its own program — there's no `wt` to attach a profile
  to). Documented in the PR.
- **#255 new-tab** (`-w 0`) composes: `-p` is inserted after `new-tab` in both forms.
- **`PlanAppLaunch`** is explicitly excluded (null profile).
- The **fallback chain survives**: `-p` only decorates the `wt` candidate; `pwsh`/`powershell`/`cmd`
  are unchanged, so a failed profile launch still falls through.

## Tests

- `WindowsTerminalProfileMatcherTests` — JSONC comments/trailing commas; `defaults` never matches;
  hidden skipped; absent/null `startingDirectory` skipped; `%ENV%` expansion; `/`-vs-`\` and
  trailing-slash and case normalisation all match; first-in-order wins; guid preferred over name;
  object-`list` and bare-array shapes; malformed/empty ⇒ null.
- `WindowsTerminalSettingsTests` — `CandidatePaths` order given `LOCALAPPDATA`; `Load` returns the
  first existing; all-missing ⇒ null; blank `LOCALAPPDATA` ⇒ empty candidates.
- Planner (`TerminalLauncherTests`) — new-window and new-tab `wt` specs include `-p <profile>` when
  set; **absent** (byte-identical) when null; `PlanAppLaunch` never emits `-p`.
- `AgentDispatchSettingsTests` — `IsDefault` false once `TryUseWindowsTerminalProfiles` is on;
  `ToLauncherOptions` unaffected.
- `DispatchCoordinatorTests` — `Plan` sets `WindowsTerminalProfile` on a match (injected settings +
  matching working dir); null when the toggle is off; null on no match; **not read** for a one-off;
  `WindowsTerminalProfileNote` text.

TUI glue (the F2 button, the host status-line append) isn't CI-unit-testable per CLAUDE.md — verified
by build + reasoning; the real `wt -p` launch is the Phase-0 manual-Windows leg noted above.

[#462]: https://github.com/rbcministries/clickup-todo-cli/issues/462
