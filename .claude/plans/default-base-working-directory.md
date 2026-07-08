# A2 — Default/base working directory (#92, part of epic #90)

## Goal

Give the app a single **base working directory** — the root where the user's ClickUp-tracked
work lives locally. It is:

- the root the Dispatch file-tree browser (#95/D3) will hang off, and
- the parent directory a task-derived launch (#98/T1) will start in.

Blank/absent ⇒ the default `~/ClickUp-Tasks`.

This issue is **foundation only**: it adds the config field, the first-run + F2 UI to set it,
and the pure resolution helper. No dispatch code consumes it yet (that's #95/#98).

## Design decisions

- **Semantics vs `AgentDispatchSettings.FixedWorkingDirectory`** (the issue's "overlap to
  reconcile"): keep them **separate**, as the issue recommends. `Fixed` means "always start in
  exactly this dir" (an explicit override mode). The new **base dir** is a *root* the browser
  and the task-derived parent build on — it is *not* a working-directory mode. The F2 help text
  states this so the two dir settings aren't confused.
- **Storage sentinel** mirrors the `AgentDispatch = new()` default-on-missing pattern: the new
  `DefaultWorkingDirectory` string defaults to `""`; blank/absent is the sentinel meaning "use
  `~/ClickUp-Tasks`", resolved at **read time**. No config migration is needed (a missing key
  deserializes to `""`, which already resolves to the default), so old configs stay
  backward-compatible with zero schema bump.
- **`~`-expansion happens at write time** (wizard + F2 save) so an absolute path is stored;
  the read-time resolver also expands `~` defensively so a hand-edited `config.json` value works.
- **Helper location**: pure logic lives in `Tui/Screens/SettingsForm.cs` (the repo's designated
  home for unit-tested settings parsing), per the issue text. `SetupWizard` (console flow) calls
  the same static helpers — they are pure string methods, so no Terminal.Gui is pulled in.

## Phase 1 — config field + pure helpers + tests

- `AppConfig.DefaultWorkingDirectory` — new `string` property, default `""`. camelCase in JSON.
- `SettingsForm`:
  - `const string DefaultWorkingDirectoryFolderName = "ClickUp-Tasks"`.
  - `string ExpandHomePath(string?, string home)` — trims; `""`→`""`; `~`→home; `~/x`/`~\x`→
    `Path.Combine(home, x)`; anything else returned trimmed as-is.
  - `string ResolveDefaultWorkingDirectory(string? stored, string home)` — read-time resolver:
    expand `stored`; blank ⇒ `Path.Combine(home, DefaultWorkingDirectoryFolderName)`.
- Tests:
  - `SettingsFormTests`: `ExpandHomePath` (blank, `~`, `~/sub`, `~\sub`, absolute unchanged,
    whitespace-trim) and `ResolveDefaultWorkingDirectory` (blank→default, `~`-value→expanded,
    absolute-value passthrough) — all built with `Path.Combine` so they're platform-agnostic.
  - `ConfigStoreTests`: round-trip the new field; camelCase key present in JSON; backward-compat
    (old `config.json` without the key loads to `""` and resolves to the `~/ClickUp-Tasks` default).

## Phase 2 — first-run wizard + F2 settings (build-verified TUI glue)

- `SetupWizard`: add a step after the refresh interval prompting for the base working directory,
  noting blank ⇒ `~/ClickUp-Tasks` and whether the entered path exists yet. Store the expanded
  value (blank stays blank ⇒ default at read time).
- `SettingsScreen`: add a left-column "Default working directory" `TextField` prefilled from the
  stored value, with a one-line note on its semantics (root, ≠ Fixed dir). On Save, expand `~`
  and surface it via `SettingsResult`.
- `SettingsResult` gains `string DefaultWorkingDirectory`; `TodoApp.OpenSettings` passes the
  stored value in and persists the result. No new focusable pane — one more field on the existing
  single settings screen.

## Verification

- `dotnet build -c Release` (0/0) + `dotnet test -c Release` (green; integration skipped w/o token).
- `dotnet format` clean.
- TUI: build + reasoning per the repo rule. Manual check notes in the PR (run `--reset`, complete
  the wizard leaving the dir blank → `config.json` has `"defaultWorkingDirectory": ""`; set one in
  F2 → persisted and reloads prefilled).

## Deferred (owned by other issues)

- Consuming the base dir as the file-tree browser root — #95 (D3).
- Using it as the task-derived launch parent — #98 (T1).
- Directory-existence *validation*/creation at launch — the wizard only notes non-existence; the
  dir is created on first task-derived launch (#98).
