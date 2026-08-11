# Dispatch pane: derive the working directory into the pre-filled field (#533)

Follow-up to #461/#469 (the `{base}/{Repository}` match) and #98/#140 (the `./{custom-id}`
output-subdir). Make the Ctrl+A Dispatch pane's working-dir field the **single place** a task-derived
directory is decided: pre-filled, visible, and editable before launch — instead of two invisible
mechanisms applied silently at launch time inside `DispatchCoordinator.Plan`.

All five owner decisions in the issue are settled; this plan implements them.

## The shape of the change

Today `Plan` derives the directory (the #461 repo match) and the prompt's `./{custom-id}` instruction
(#98) behind the user's back and reports them afterwards via a status note. This moves **all**
derivation into the pane **pre-fill**, so:

- The field opens pre-filled with the derived directory; submitting unchanged launches there.
- Clearing the field launches in the plain base dir (the user rejecting the auto-detection — honoured).
- `./{custom-id}` becomes a real **directory** the app creates, not a prompt instruction.
- `Plan` goes back to pure (no filesystem probes except the unrelated #462 WT-profile lookup).

## Owner decisions (settled on the issue) — implemented as

1. **A cleared field means the base dir.** `Plan` stops deriving: a blank
   `DispatchRequest.WorkingDirectory` in task-derived mode resolves to the plain base dir. All
   derivation lives in the pre-fill.
2. **`./{custom-id}` becomes a directory, not an instruction.** In task-derived mode with no repo
   match the pre-fill is `{base}/{custom-id}`; the `{outputDirInstruction}` paragraph is no longer
   emitted; the app creates the directory at launch.
3. **Pre-fill precedence** (task-derived mode): #96 per-task cache → `{base}/{Repository}` match →
   `{base}/{custom-id}`. Blank only if none applies or the user clears it.
4. **`Home` / `Fixed` modes are untouched.** No derivation pre-fill; the field is blank (⇒ "use my
   configured mode"), except the existing #96 cache pre-fill, which is mode-independent and preserved.
5. **Drop `RepositoryMatchNote`.** A visible pre-filled field explains itself; the silent-change note
   is redundant. (Overlaps #517 — coordinate, don't duplicate.)

## New pure helper — `Tui/DispatchWorkingDirectoryPreFill`

The one place derivation lives now. Pure; the filesystem is injected (real-FS defaults), mirroring
`RepositoryWorkingDirectory` / the old `Plan` probes. Placed in `Tui` (with `DispatchCoordinator`, also
pure + unit-tested) rather than `Services`, so it can reuse `SettingsForm.ExpandHomePath` without
Services taking a Tui dependency.

- `PreFill(cache, taskId, detail, settings, baseDir, dirExists?, childDirNames?)` → the field value:
  #96 cache (non-blank) → else, **task-derived only**, `TaskDerivedDefault` (repo match ⇒ its checkout,
  else `{base}/{custom-id}`) → else `""` (Home/Fixed, or nothing applies).
- `AutoDerivedDefault(detail, settings, baseDir, home, dirExists?, childDirNames?)` → the directory an
  **accepted-unchanged** pre-fill resolves to — the #96 cache-reconciliation baseline. Task-derived:
  `TaskDerivedDefault` (**excludes** the cache — that would be circular). Home/Fixed: the configured
  dir (`~`-expanded), byte-identical to today's `resolvedDefault`.

`TaskDerivedDefault` and the field pre-fill call `RepositoryWorkingDirectory.Resolve` /
`AgentPromptComposer.OutputSubdirectoryToken` with identical inputs, so the field value and
`AutoDerivedDefault` produce the **same string** for the accept-unchanged case → the #96 cache is
cleared, not poisoned, on every dispatch (the subtle invariant the issue flags).

## `DispatchCoordinator.Plan` — pure again

- Drop `directoryExists` / `childDirectoryNames` params and the `RepositoryWorkingDirectory.Resolve`
  call; the task-derived candidate is just the base dir. A blank field ⇒ base dir (decision 1).
- Drop `OutputSubdir`, `ResolvedDefault`, `RepositoryDir` from `ResolvedDispatch`; drop the
  `RepositoryMatchNote` helper (decision 5).
- Replace `UseTaskDerived` (base-dir-creation gate) with **`CreateWorkingDir`**: create the resolved
  working dir when it **lies inside the base working-directory tree** (inclusive) — a pure path
  containment check. Covers the base dir, the `{custom-id}` subdir, a matched checkout and any
  browsed-to subdir; never a Home/Fixed dir outside the tree, nor an out-of-tree typo. (The rejected
  alternatives — always-create, or a provenance flag threaded through `DispatchRequest` — are named
  here per the issue.)
- Keep the #462 WT-profile lookup (the one remaining I/O seam, injected).
- `ReconcileCache(cache, taskId, chosenDir, resolvedDefault)` now takes `resolvedDefault` explicitly
  (the host computes it via `AutoDerivedDefault`), since `Plan` no longer probes the filesystem.

## `{outputDirInstruction}` becomes dead — retire `outputSubdir`

- Retire the `outputSubdirectory` parameter from `AgentPromptComposer.Compose` / `WritePromptFile` and
  `AgentDispatcher.DispatchAsync` / `DispatchBackgroundAsync` (its only producer is gone).
- Keep the `{outputDirInstruction}` placeholder resolving to the **empty string** so a saved custom
  template (#100) containing it still renders (to nothing) rather than failing; drop it from the
  documented `Placeholders` token list.
- `OutputSubdirectoryToken` **survives** — it now names the pre-fill *directory* segment.

## Host wiring (`TodoApp`, `SingleTaskApp`)

- The `workingDirectoryPreFill` delegate calls `DispatchWorkingDirectoryPreFill.PreFill(...)` with the
  live cache, the detail, the settings and the already-computed base dir — one shared helper so the two
  hosts can't drift.
- `DispatchAgent` computes `resolvedDefault = AutoDerivedDefault(...)` and passes it to
  `ReconcileCache`.

## Out of scope (own follow-up issue)

Renaming the now-misleading `AgentWorkingDirectory.TaskDerived` / retiring the orphaned
`AgentDispatchSettings.UsesTaskDerivedOutput` (it loses its only production consumer here) — a tidy-up
tracked in its own issue, kept out of this PR to keep it focused. The seeding of the directory browser
to the derived dir is a small optional nicety; the field is free text and carries the value regardless,
so it is deferred with the rename.

## Tests

- `DispatchWorkingDirectoryPreFillTests` — cache precedence; repo match ⇒ checkout; no match ⇒
  `{base}/{custom-id}`; custom-id → id fallback; Home/Fixed ⇒ blank; cache pre-fill preserved in
  Home/Fixed; `AutoDerivedDefault` per mode; field == `AutoDerivedDefault` (accept-unchanged invariant).
- `DispatchCoordinatorTests` — rewritten: cleared field ⇒ base + `CreateWorkingDir` true; a
  `{base}/sub` pick ⇒ `CreateWorkingDir` true; an out-of-tree pick / Home mode ⇒ `CreateWorkingDir`
  false; no repo match / no output subdir in the plan; `ReconcileCache` new signature; WT-profile legs
  unchanged.
- `AgentPromptComposerTests` / `AgentDispatcherTests` — remove the retired `outputSubdirectory` cases;
  add a guard that `{outputDirInstruction}` in a custom template renders to empty.
- `dotnet test` green; `dotnet format` clean. `tui-validate` for the Dispatch pane pre-fill/status
  (a pre-filled field *value* is data, but decision 5 drops the status note — a reporting change).
