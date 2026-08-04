# Dispatch: launch in a `{base}/{Repository}` sub-dir when one exists (#461)

Part of the Dispatch working-directory work (epic #90). Prerequisite to the
Windows-Terminal-profile matching in #462.

## Goal

When Dispatch is in **task-derived** working-directory mode (the default) and the
task's custom fields include a **`Repository`** field with a value, look for a
**direct child** of the base working directory whose name matches that value. If
one exists, start the session **in that sub-directory** instead of the base dir.
No `Repository` field, no value, or no matching sub-directory ⇒ behave exactly as
today (strict no-op).

On a repo match the per-task `./{custom-id}` output-subdir instruction is **not**
emitted (owner decision, recorded on #461): the session is already inside the
checkout, so scaffolding a scratch folder in the working tree would litter
`git status`.

## Verified current state (`67e7729`)

- `DispatchCoordinator.Plan` (`Tui/DispatchCoordinator.cs:57`) is pure and the single
  resolution path both hosts (`TodoApp`, `SingleTaskApp`) share. It already has the
  full `TaskDetail`, so `detail.CustomFields` (`ClickUp/Models.cs:422`,
  `IReadOnlyList<CustomFieldItem>` of `Name`/`Type`/`JsonElement? Value`/`Options`)
  are in hand — no API round-trip, no Kiota regen.
- `settings.UsesTaskDerivedOutput(chosenDir)` is one boolean meaning both
  "task-derived mode, no explicit pick" (base-dir creation) **and** "emit the
  output-subdir instruction". `Plan` uses it for both `UseTaskDerived` (base-dir
  creation in `RunInteractive`/`RunBackground`) and to gate `OutputSubdir`.
- `resolvedDefault` (`DispatchCoordinator.cs:78`) is "the dir the configured mode
  would use with no pick", fed to the #96 per-task cache reconciliation.
- The status the user is left looking at is `AgentDispatchResult.StatusMessage`
  (`Launched Claude (<term>) for '<task>'.`) — it does **not** name the working dir.

## Design decisions

- **Pure matcher, filesystem injected.** New `Services/RepositoryWorkingDirectory`
  with a pure `Resolve(detail, baseDir, dirExists, childDirNames)` (mirrors
  `TerminalCommandPlanner`'s injected `Func exists`) so the match logic is
  unit-tested against in-memory directory sets. The real-FS probes
  (`Directory.Exists` / a guarded `Directory.GetDirectories`) live as `Plan`
  defaults in `DispatchCoordinator`, keeping the matcher 100% pure.
- **Value extraction is raw, not display.** A small pure extractor reads the
  `Repository` field (name matched **case-insensitively**) and yields a raw string
  for the plausible types — `text`/`short_text`/`url`/`email`/`phone` (the string
  value), `drop_down` (the selected option's name), and `labels` **only when
  exactly one** label is selected (its name). Anything else ⇒ no value. It does
  **not** reuse `TaskDetailFormatter.CustomFieldValue` (200-char truncation, comma
  joins, `Tui/`).
- **Normalisation to one segment.** Accepts a bare name, `owner/repo`, a full
  `https://github.com/owner/repo` URL, and a trailing `.git`: take the last path
  segment (URL path or `/`-`\`-split), strip a trailing `.git`. **Traversal is
  impossible**: the result must be a single bare name — reject `.`/`..`, rooted
  paths, directory separators, and invalid filename chars; a final
  `Path.GetDirectoryName(GetFullPath(base/candidate)) == GetFullPath(base)`
  containment check backstops it.
- **Match = exact-then-case-insensitive.** Prefer an exact child (`dirExists`), else
  a case-insensitive scan of the base dir's immediate children so `my-repo` finds
  `My-Repo` on Linux too. **Direct-child only** (no recursive scan — latency on a
  keypress path). A **file** child never matches (probes are directory-only). Never
  creates a directory.
- **Split the two flags.** Keep `UseTaskDerived` (mode / base-dir creation) exactly
  as today — it stays `true` on a repo match, and creating the already-existing repo
  dir is harmless. Emit the output subdir only when `UseTaskDerived && no repo
  match`, i.e. `OutputSubdir` becomes `null` on a match ⇒
  `OutputDirInstruction` renders empty ⇒ the prompt is byte-identical (default and
  custom `PromptTemplate` alike) to a match-free dispatch minus that paragraph.
- **`resolvedDefault` reflects the match.** In `TaskDerived` mode the repo-matched
  dir is passed as the `taskDerivedDirectory` candidate to **both**
  `ResolveEffectiveWorkingDirectory` (the launch dir) and `ResolveWorkingDirectory`
  (the resolved default), so an explicit pick equal to the matched dir still clears
  the #96 cache via `ReconcileCache`. `Home`/`Fixed` ignore the candidate, so they
  are entirely unaffected. Compute the match only in `TaskDerived` mode.
- **Surface the directory.** `ResolvedDispatch` gains `RepositoryDir` (the matched
  dir, non-null only when the match actually drove the working dir — i.e. no
  explicit pick). A pure `DispatchCoordinator.RepositoryMatchNote(plan)` returns a
  status suffix; `RunInteractive` appends it to the reported message so the user can
  tell why the session opened where it did.
- **Field name hard-coded** `Repository` (case-insensitive); not a setting until a
  second convention appears (decision recorded here).

## Phase 1 — pure matcher + value extractor + unit tests

- `Services/RepositoryWorkingDirectory`: `RepositoryValue(detail)`,
  `NormalizeSegment(raw)`, `Resolve(detail, baseDir, dirExists, childDirNames)`
  returning `Match?(Directory, Name)`.
- `RepositoryWorkingDirectoryTests`: value extraction per type (text, url,
  drop_down option name, single-label, multi-label ⇒ none, wrong type ⇒ none,
  case-insensitive field name, blank/whitespace ⇒ none); normalisation (bare,
  `owner/repo`, GitHub URL, trailing `.git`, trailing slash); traversal rejection
  (`..`, `../..`, absolute, separator-bearing, invalid chars); match (exact,
  case-insensitive child, file-not-dir ⇒ none, missing ⇒ none, no children ⇒ none).

## Phase 2 — `DispatchCoordinator.Plan` wiring + coordinator tests

- `ResolvedDispatch` gains `string? RepositoryDir`.
- `Plan` grows two optional FS-probe params (default real FS); in `TaskDerived`
  mode compute the match, thread the matched dir as the task-derived candidate,
  suppress `OutputSubdir` on a match, set `RepositoryDir`.
- `RepositoryMatchNote(plan)` pure helper; `RunInteractive` appends it (glue).
- `DispatchCoordinatorTests`: repo match drives working dir + suppresses subdir +
  keeps `UseTaskDerived` true; no match ⇒ byte-identical to today; explicit pick
  still wins but `resolvedDefault` reflects the match so an equal pick reverts the
  cache; `Home`/`Fixed` unaffected; the note text.

## Verification

- `dotnet build -c Release` (0/0), `dotnet test -c Release` (green; integration
  self-skips w/o `CLICKUP_TOKEN`), `dotnet format` clean.
- Pure logic ⇒ fully unit-tested. `Plan` is CI-testable; the one-line
  `RunInteractive` append is build-verified glue (per CLAUDE.md). `tui-validate`
  only if the Dispatch pane / status reporting output changes — it does not
  (the status note is data, not layout), so a run is optional; noted in the PR.
- No `Generated/` edits, no spec change, no new focusable pane.

## Out of scope

Windows-Terminal profile matching (#462, stacks on this); renaming the
`TaskDerived` mode; recursive/fuzzy repo discovery.
