# E2E: PTY check for the Dispatch pane working-dir browser seeding (#564)

Follow-up to #559 (PR #563), covering its acceptance criterion #4's dedicated
`tui-validate` PTY check — deferred there because a seed scenario needed new
harness scaffolding on `Program.cs`, the merge-conflict hotspot #489 was
shrinking. #489 (PR #566) has since landed the `IE2EScenario` + reflection
discovery model, so the seed scenario is now **one self-contained file** rather
than another append point.

## What shipped in #559 (the behaviour under test)

Since #559, `TaskDetailScreen.ShowPrompt` (the Ctrl+A Dispatch pane) computes the
task-derived pre-fill once, keeps it in the working-dir field, and calls
`DirectoryBrowserModel.SeedTo(preFill)` — which, **when the pre-fill resolves to
an existing directory**, moves the browser to the target's *parent* and returns
the target's own entry name for the `ListView` to highlight, so `↑`/`↓` start
among the target's siblings with the target selected. A blank / not-yet-existent
/ base-root / filesystem-root target degrades to `Reset()` (base root, no
highlight) — today's behaviour, field preserved.

`DirectoryBrowserModel.SeedTo` is already fully unit-tested against scratch dirs
(`DirectoryBrowserModelTests`). What is **not** yet covered is the *seeded path
end-to-end* on the real rendered screen: opening the pane on a task whose
pre-fill resolves to an existing directory and asserting, on the pyte screen,
that the browser opened at the target's parent with the target highlighted while
the field still shows the pre-fill.

## The change (test-only — no app code)

The feature is already on `main`; this issue adds coverage only.

### 1. Harness scenario — `Scenarios/DispatchSeedScenario.cs`

A new `IE2EScenario`, active on `E2E_DISPATCH_SEED=1`, self-contained in one file
(nothing else edited). Its `Configure(AppConfig)`:

- Stands up a unique temp **base working dir** on disk with a small directory
  tree whose names are distinctive uppercase tokens so they are unambiguous on
  the pyte screen and never collide with task titles / UI chrome:

  ```
  {base}/
    AAAROOTKID/
    WTPROJECTS/
      SEEDTARGET/
      SIBLINGONE/
      SIBLINGTWO/
    ZZZROOTKID/
  ```

- Points `config.DefaultWorkingDirectory` at `{base}` (so `TaskDetailScreen`'s
  browser roots there and the pre-fill's `baseDirectory` is `{base}`).
- **Seeded mode** (default): seeds the #96 per-task cache
  (`config.TaskWorkingDirectories`) for a wide range of default task ids →
  `{base}/WTPROJECTS/SEEDTARGET`, an **existing nested** directory. The #96
  cached-dir path is one of the three sources `DispatchWorkingDirectoryPreFill`
  honours (issue #564 explicitly sanctions "a `{base}/{Repository}` checkout
  match, **or** the #96 cached dir"); a **nested** target is chosen so the
  browser's *parent-of-target* listing (`SEEDTARGET`/`SIBLINGONE`/`SIBLINGTWO`)
  is observably different from the base-root listing
  (`AAAROOTKID`/`WTPROJECTS`/`ZZZROOTKID`) — a robust screen signal beyond the
  selection highlight alone.
- **Degrade mode** (`E2E_DISPATCH_SEED_DEGRADE=1`): the same base tree, but the
  cache is **not** seeded, so the task-derived pre-fill is `{base}/{custom-id}`
  (here `{base}/{taskId}`), which does **not** exist on disk — reproducing the
  "not-yet-existent target" degrade path exactly.

Wide-seeding the cache (rather than a single known id) keeps the check robust to
whichever task the first list row resolves to — no dependence on row ordering.

### 2. PTY check — `dispatch_dir_browser_seed_check.py`

Mirrors the existing detail-screen checks (`detail_arrow_check.py` et al.): boots
the real dashboard under a PTY, opens the first task's detail, and drives Ctrl+A
to open the Dispatch pane. Two legs, each its own boot:

- **Seeded leg** (`E2E_DISPATCH_SEED=1`): asserts the working-dir field carries
  the pre-fill `…/WTPROJECTS/SEEDTARGET`; the browser shows the *parent-of-target*
  listing (`SEEDTARGET`, `SIBLINGONE`, `SIBLINGTWO` present; the base-root
  siblings `AAAROOTKID`/`ZZZROOTKID` absent); and the highlighted row is
  `SEEDTARGET` (detected via the selected-row background fill — the pane opens
  with focus on the prompt field, so the browser `ListView` is unfocused and its
  selected row draws with Terminal.Gui's `VisualRole.Active` fill).
- **Degrade leg** (`E2E_DISPATCH_SEED=1 E2E_DISPATCH_SEED_DEGRADE=1`): asserts the
  browser opened at the **base root** (`AAAROOTKID`, `WTPROJECTS`, `ZZZROOTKID`
  present; `SEEDTARGET`/`SIBLINGONE` absent) with the `..` row highlighted, and
  the field carries the non-existent `{base}/{taskId}` pre-fill (never clobbered).

## Tests

- No new unit tests: `SeedTo` / `PreFill` decision logic is already unit-tested
  (`DirectoryBrowserModelTests`, `DispatchWorkingDirectoryPreFillTests`); this
  slice is the missing *end-to-end rendered* proof.
- `dotnet build clickup-todo.slnx -c Release` 0/0; `dotnet test` green (the E2E
  harness only gains a scenario file; no app change).
- `tui-validate`: the new `dispatch_dir_browser_seed_check.py` (both legs) plus a
  regression pass of `detail_check.py` A/B (the Dispatch pane is hidden until
  Ctrl+A, so the detail render is untouched and stays byte-identical).

## Out of scope

The `{base}/{Repository}` (#461) derivation path is exercised by
`RepositoryWorkingDirectory` unit tests; this check uses the #96 cached-dir
source (sanctioned by #564) because it lets the existing target be **nested**,
which makes the seeded-vs-root distinction unambiguous on the rendered screen.
