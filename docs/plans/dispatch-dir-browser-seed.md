# Dispatch pane: seed the directory browser to the task-derived pre-fill directory (#559)

Cosmetic follow-up deferred from #533 and again from #551 — split out to keep those PRs clean. Small,
self-contained TUI-glue slice with the decision-bearing logic factored into a pure, unit-tested model
method.

## Background

Since #533 the Ctrl+A Dispatch pane's working-dir field opens **pre-filled** with the task-derived path
(`{base}/{Repository}` match #461, else `{base}/{custom-id}` #98, else the #96 per-task cache), computed
by `DispatchWorkingDirectoryPreFill.PreFill`. The file-tree **browser** beside the field, however, still
opens rooted at the base dir (`TaskDetailScreen.ShowPrompt` → `_browser.Reset()`), highlight on `..`, so
`↑`/`↓` don't start "in the right place" relative to the pre-filled field.

## The change

Seed the browser to the pre-filled directory when the pane opens, so arrow navigation begins there —
**but only when the pre-fill resolves to an existing directory**. A `{base}/{custom-id}` target usually
does not exist yet (it is created at launch), and the field is free text that carries the value
regardless; this issue is only about the browser's starting position when the target *does* exist (a
`{base}/{Repository}` checkout match, or the #96 cached dir).

Two moving parts:

1. **Pure model — `DirectoryBrowserModel.SeedTo(string? targetDirectory)`** (the one filesystem-touching
   piece, already fully unit-tested against scratch dirs). Given a pre-fill value it:
   - moves the browser to the target's **parent** and returns the target's own entry **name** for the
     ListView to highlight — so `↑`/`↓` begin among the target's siblings with the target selected, and
     the highlighted row's `SelectionPathAt` matches the pre-filled field;
   - degrades to `Reset()` (base root, no highlight → returns `null`) when the target is blank, does not
     currently exist, is the base root itself, or is a filesystem root (no parent listing to highlight
     in). These are exactly the "graceful, field preserved" cases the acceptance criteria call out.
   - `_root` is unchanged, so `Reset()` still returns to the base working dir (#92) and the browser's
     rooted-at-base contract holds.

2. **Glue — `TaskDetailScreen.ShowPrompt`.** Where it currently does `_browser.Reset(); RefreshBrowser();`
   under the `_suppressWorkingDirSync` guard, it now computes the pre-fill once, keeps it in the field,
   calls `SeedTo(preFill)`, and highlights the returned name via the existing
   `RefreshBrowser(selectEntry:)` path. The guard already suppresses the selection-follows-cursor sync
   the seed's highlight fires, so **the pre-filled field value is never clobbered** — the crux of the
   `_suppressWorkingDirSync` interaction the issue flags. `SeedTo` performs the `Reset` for the
   degrade-to-root cases, so blank/non-existent pre-fills reproduce today's behaviour exactly.

Nothing else changes: no new focusable pane (#3), no generated code, no spec/API change. `Plan` and the
pre-fill precedence (#533) are untouched — this is purely where the browser's cursor starts.

## Tests

- **Unit (`DirectoryBrowserModelTests`)** — `SeedTo`: existing direct child of root → highlights it, still
  rooted at root; existing nested target → moves to its parent, highlights the leaf; non-existent target
  (incl. after navigating away) → resets to root, `null`; blank/whitespace/`null` → root, `null`; target
  equal to the root → root, `null`; an existing target outside the base tree (the #96 cached-dir case) →
  moves there; and `Reset()` after a seed still returns to the base root (`_root` intact).
- **TUI glue** — per CLAUDE.md, Terminal.Gui glue is not CI-unit-testable; the `ShowPrompt` change is a
  thin call into the tested model. Verified by build + reasoning and the existing detail-screen
  `tui-validate` checks (the Dispatch pane renders unchanged for the common blank/non-existent pre-fill,
  which still resets to root). A dedicated PTY seed scenario would require standing up a base working dir
  with a real repo-named subdir plus a matching task in the fake backend — new harness scaffolding on the
  `Program.cs` merge-conflict hotspot that #489 is actively trying to shrink — so it is deferred rather
  than piled onto that file now; noted in the PR.

## Out of scope / inherent limitation (from #533 / #551)

A `{base}/{custom-id}` target typically doesn't exist before first launch, so the browser can't highlight
it — the field still carries the value. This slice only seeds the cursor when the target exists.
