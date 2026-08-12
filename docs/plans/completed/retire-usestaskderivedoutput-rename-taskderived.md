# Tidy-up: retire `UsesTaskDerivedOutput` + rename `AgentWorkingDirectory.TaskDerived` (#551)

Deferred tidy-up from #533 (merged via #550), which named it out-of-scope. Pure-refactor slice:
no behaviour change, no new user-facing surface.

## Background

#533 moved all task-derived Dispatch working-directory derivation into the pre-filled pane field
(`DispatchWorkingDirectoryPreFill`) and made `DispatchCoordinator.Plan` pure. Two names were left
behind, now misleading:

1. **`AgentDispatchSettings.UsesTaskDerivedOutput(chosenDirectory)`** — gated the retired
   per-task `./{custom-id}` *output-subdir instruction* on "task-derived mode **and** no explicit
   pane pick". Post-#533 the directory-creation decision is the pure `ResolvedDispatch.CreateWorkingDir`
   containment rule (`DispatchCoordinator.cs:109`), and the subdir is a real directory, not an
   instruction. `grep` confirms the method has **no production consumer** — only its own definition
   (`AgentDispatchSettings.cs:243`) and its unit tests reference it.
2. **`AgentWorkingDirectory.TaskDerived`** — the identifier implies `Plan`/launch derives the
   directory. It no longer does: derivation is the pre-fill's job, and a cleared field launches in the
   plain base dir. The mode is really "start in the base working directory, with a task-derived
   pre-fill in the pane."

## Constraints

- **Config wire-compat is non-negotiable.** `AgentWorkingDirectory` is persisted to `config.json`
  **by name** via the global `JsonStringEnumConverter` (`Configuration/StateJson.cs:21`). A naive
  C# rename would (a) fail to deserialize existing configs holding `"workingDirectory": "TaskDerived"`
  and (b) start writing a different string. So the rename must **pin the JSON value** to `"TaskDerived"`
  with `[JsonStringEnumMemberName("TaskDerived")]` (System.Text.Json 9+, honored by the reflection
  `JsonStringEnumConverter`; target is `net10.0`). Net effect on disk: **zero** — same string read and
  written, no migration.
- **Never weaken a test.** The `UsesTaskDerivedOutput_*` tests cover behaviour that no longer exists
  (the #95 explicit-pick subdir suppression, retired by #533). They are **obsolete**, not weakened —
  remove them with the method rather than loosen an assertion.
- Build stays 0 warnings / 0 errors — so every `<see cref="…TaskDerived"/>` doc reference must move to
  the new name too, or CS1574 fails the Release build.
- No behaviour change: the user-facing Settings label stays **"Task-derived"** (it comes from a
  `switch`, `SettingsScreen.cs:384`, not the enum name), and `IsDefault` / `ResolveWorkingDirectory`
  keep their exact semantics.

## Plan (single phase — a small, self-contained refactor)

### 1. Retire `UsesTaskDerivedOutput`
- Delete the method (`AgentDispatchSettings.cs:234-244`) and its XML doc.
- Delete its test region (`AgentDispatchSettingsTests.cs:291-319` — the three
  `UsesTaskDerivedOutput_*` facts/theories).

### 2. Rename `AgentWorkingDirectory.TaskDerived` → `BaseWithTaskPrefill`
- Rename the enum member; annotate `[JsonStringEnumMemberName("TaskDerived")]` to keep the wire value.
- Rewrite the member's doc comment to describe the post-#533 behaviour (base dir; task-derived
  pre-fill; cleared field ⇒ plain base dir; derivation is the pre-fill's job, not launch's).
- Update every reference:
  - src: `AgentDispatchSettings.cs` (default init, `IsDefault`), `SettingsScreen.cs`
    (`WorkingDirOrder`), `DispatchCoordinator.cs` (`createWorkingDir` gate),
    `DispatchWorkingDirectoryPreFill.cs` (two mode checks), and doc `cref`s in
    `Agent/AgentPromptComposer.cs` and `Tui/TodoApp.cs`.
  - tests: the direct `AgentWorkingDirectory.TaskDerived` uses in `AgentDispatchSettingsTests.cs`.
    Scenario-descriptive test names/comments elsewhere (`DispatchCoordinatorTests`,
    `DispatchWorkingDirectoryPreFillTests`, `AgentDispatcherTests`) still read as "task-derived mode"
    and reference the default via `new AgentDispatchSettings()`, so they need no code change.
- Add a **wire-compat regression test**: an old config JSON with `"workingDirectory": "TaskDerived"`
  deserializes to `AgentWorkingDirectory.BaseWithTaskPrefill`, and re-serializing writes `"TaskDerived"`.

### 3. Quality gate
- `dotnet build -c Release` (0/0), `dotnet test -c Release` green, `dotnet format`.
- No TUI/rendering change ⇒ `tui-validate` not required (label text and layout unchanged).

## Deferred (tracked separately)

- **Seed the directory browser to the derived directory** (the "Also deferred from #533" nicety):
  cosmetic TUI glue interacting with `_suppressWorkingDirSync`, and — as #533/#551 note — it can't even
  highlight the common `{base}/{custom-id}` case (that dir doesn't exist yet). Left out to keep this a
  clean, fully-unit-tested pure refactor; filed as a follow-up issue and linked from the PR.
