# Plan — Split-pane (L, #517): one coherent dispatch status line

Slice **L** of the Split-pane epic (#502). Depends on **J (#515)** — merged (PR #588), which added
`ResolvedDispatch.SplitDegradedReason` and the `RunInteractive` prepend that surfaces a split→tab
degradation. Folds in the two other facts a dispatch status line carries: the **#462** Windows
Terminal profile note (merged, PR #531) and the **#461** repo-matched directory (merged) — see the
note below on why #461 is *not* a status-line clause anymore.

Pure formatting over already-computed facts, per #517's scope boundary: **no** change to what is
launched, which directory is resolved, or which profile is matched. If a change here would alter
launch behaviour it belongs in J / #461 / #462, not here.

## Verified current state (`main`)

The dispatch status line the user sees after an interactive dispatch is assembled **ad-hoc** in
`DispatchCoordinator.RunInteractive` (`Tui/DispatchCoordinator.cs:314-315`):

```csharp
var degraded = plan.SplitDegradedReason is { } reason ? reason + " " : "";
var status = degraded + result.StatusMessage + (WindowsTerminalProfileNote(plan, result.LaunchedWith) ?? "");
```

So today there are two clauses wrapped around the launcher's core message:

- **core** — `result.StatusMessage`, built by `AgentDispatcher.FormatStatus`
  (`Agent/AgentDispatcher.cs:165-173`): on success `"Launched Claude ({LaunchedWith}) for '{task}'."`
  with any non-fatal `LaunchResult.Note` (the split→tab→window launcher fallback, e.g. `"Opened a new
  window (no tab support)."`) appended; on failure `"Could not launch Claude: {error}"`.
- **#515 split-degraded reason** (`plan.SplitDegradedReason`) — **prepended**. The viability-floor
  string from `SplitViability.Evaluate` (`Agent/SplitViability.cs:81`): `"Terminal too narrow to split
  ({resulting}-column panes; need {floor}) — opening elsewhere instead."` (host-agnostic per #590).
- **#462 WT-profile note** (`WindowsTerminalProfileNote(plan, launchedWith)`,
  `Tui/DispatchCoordinator.cs:231-236`) — **appended**, gated on a Windows Terminal host actually
  launching: `" (Windows Terminal profile '{profile}'.)"`.

**Why #461 is not a clause.** The repo-matched-directory note (`RepositoryMatchNote`) was **removed by
#533**: the directory it chose is now visible in the pre-filled working-dir field, which explains
itself. There is no directory clause in the status line today and this slice does not reintroduce one —
so #517's "three clauses" is, on current `main`, **two** live clauses (degradation + profile) around
the core message. The composition is written to make that explicit rather than to resurrect the
directory note.

## The decision — how the line composes

One pure formatter, `Agent/DispatchStatusLine.Compose`, owns the composition rule (today spread across
`RunInteractive`'s inline concat and the WT-note gate). The rules, in priority order:

1. **Failure short-circuits.** When the launch failed, the line is exactly the core failure message
   (`"Could not launch Claude: …"`). The degradation and profile clauses are **suppressed** — nothing
   opened, so "you got a tab instead of a pane" and "under profile X" are both moot and would mislead.
   (Profile is already suppressed on failure via the WT-host gate — `LaunchedWith` is null; this makes
   the degradation prefix follow the same rule, which the old inline concat did **not**: it would
   prepend "too narrow to split … opening elsewhere instead" in front of a *failure*.)
2. **Degradation leads.** It is the highest-value clause — "you asked for a pane and got a tab" is the
   one thing the user actively needs — so it is never buried behind the core or the profile.
3. **Core message** follows, carrying the launcher `Note` unchanged (the launcher-level split→window
   fallback warning still rides along, verbatim).
4. **Profile trails.** Lowest-value, parenthetical, and only when a Windows Terminal host actually
   launched.
5. **All-defaults ⇒ shortest honest message.** Got the destination you asked for, no profile, no
   launcher note → just `"Launched Claude ({terminal}) for '{task}'."` — no empty clauses, no
   trailing punctuation noise.

**Length.** Each clause is short and bounded (the degradation reason names two numbers; the profile is
a short parenthetical). Even the maximum — degradation + core + launcher note + profile — is one honest
status line, and every clause carries distinct information, so nothing is dropped or truncated. The
must-see clause (degradation) leads, so if a narrow terminal clips the line the user still sees the
part that explains the surprise.

## Shape of the change

- **New `Agent/DispatchStatusLine.cs`** (pure, namespace `ClickUpTodo.Agent`, sibling of
  `AppHostLaunch`/`AgentDispatcher.FormatStatus`):
  - `Compose(string coreStatusMessage, bool launched, string? launchedWith, string? splitDegradedReason,
    string? windowsTerminalProfile)` → the single composed line, per the rules above. It wraps the
    already-computed core message (respecting #517's "already-computed facts" boundary — it does not
    rebuild "Launched Claude…" or re-derive the note, keeping `FormatStatus` the single owner of the
    core text and leaving the background/one-off path untouched).
  - `WindowsTerminalProfileNote(string? windowsTerminalProfile, string? launchedWith)` → the WT-note
    clause, moved here so the profile-clause phrasing/gate lives with the rest of the composition
    (single source of truth).
- **`DispatchCoordinator.WindowsTerminalProfileNote(ResolvedDispatch, string?)`** stays as a public API
  (its `DispatchCoordinatorTests` coverage is unchanged) but **delegates** to
  `DispatchStatusLine.WindowsTerminalProfileNote(plan.WindowsTerminalProfile, launchedWith)` — no
  duplicated gate.
- **`DispatchCoordinator.RunInteractive`** replaces the inline `degraded + StatusMessage + note`
  concat with a single `DispatchStatusLine.Compose(result.StatusMessage, result.Success,
  result.LaunchedWith, plan.SplitDegradedReason, plan.WindowsTerminalProfile)` call. Both hosts
  (`TodoApp`, `SingleTaskApp`) route through `RunInteractive`, so this is the one composition site.

## Tests (pure / offline) — `tests/ClickUpTodo.Tests/DispatchStatusLineTests.cs`

Mirrors `AppHostLaunchTests` (exact-string `[Theory]`s over a pure formatter). Exhaustive over each
fact present/absent in combination:

- **all-defaults** → shortest honest message (core only).
- launcher **Note** in the core message survives composition unchanged (present with and without the
  other clauses).
- **degradation only** → reason leads, core follows.
- **profile only** → trails, and only when `LaunchedWith` is a Windows Terminal host (`"Windows
  Terminal"`, `"Windows Terminal (new tab)"` match; `"gnome-terminal"`, `"PowerShell (pwsh)"`, null do
  not).
- **degradation + core(+note) + profile** together → all present, asserted **in order** (degradation
  index < core index < profile index).
- **failure** → exactly the failure core message even when degradation/profile facts are supplied
  (both suppressed).
- whitespace/blank `splitDegradedReason` and `windowsTerminalProfile` treated as absent (no leading
  space, no empty parenthetical, no double spaces).
- `WindowsTerminalProfileNote` gate unit-tested directly (WT-host variants / non-WT / null), mirroring
  the existing `DispatchCoordinatorTests.WindowsTerminalProfileNote_*` assertions.

The existing `DispatchCoordinatorTests.WindowsTerminalProfileNote_NamesTheProfile_*` stays green
against the delegating wrapper (not weakened).

## Hard-rule compliance

- No `Generated/` edits, no `clickup-openapi.json` change, no Kiota regen (no ClickUp API surface).
- ClickUp auth quirk untouched.
- No TUI rendering / list-source / driver / keypress change — the one host edit swaps an inline string
  concat for a pure-formatter call; no new focusable pane, no new keybinding (#3/#12). So no
  `tui-validate` run is required (pure string composition; the flash text is unit-tested and the
  launch behaviour is byte-identical).
- New units are pure/offline; no integration surface, so no new `SkippableFact`.

## Note on the pre-existing CI red

`DispatchCoordinatorTests.Plan_SplitPane_NarrowTerminal_DegradesToTab_WithReason` fails on `main`
today: it asserts the `SplitDegradedReason` `Contains("tab")`, but #590 changed that reason to the
host-agnostic `"… opening elsewhere instead."` (no "tab"). Open PR **#593** corrects that assertion.
This slice touches none of that test or the `SplitViability` reason text, so it's left to #593; once
#593 merges a rebase clears it.

## Out of scope (tracked under epic #502)

- The `Ctrl+Alt+Enter` gesture / `OpenInSplitPane` app-host status lines — D/E (#506/#507), gated on
  the still-open spike A (#503).
- Cross-platform live validation on real terminals — I (#511).
</content>
</invoke>
