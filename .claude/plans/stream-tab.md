# Plan — S1: new "Stream" tab (#106)

Part of epic #102 (task detail view improvements). Adds a **Stream** tab to the
task detail view: the Description followed by the comments as a single scrollable
timeline, sortable oldest-first / newest-first via **Ctrl+PgUp / Ctrl+PgDn**, and
made the **default** tab.

## Acceptance criteria (from the issue)

- New `Stream(task, comments, sortDirection)` in `TaskDetailFormatter` — Terminal.Gui-free,
  unit-tested. Emits Description + comments as blocks separated by the shared
  `CommentSeparator` (#105 / "#C1", already merged), consistent with the Comments tab.
- Ordering defined precisely:
  - **Ascending** (oldest-first) = Description block, then comments by date ascending.
  - **Descending** (newest-first) = comments by date descending, then Description block last.
  - Undated comments have a documented, deterministic position.
- Stream tab inserted and made the **default** selected tab (replaces `_tabs.Value = description`
  at `TaskDetailScreen.cs:113`); added to `_tabContents` / `_scrollTargets` so Tab-cycling
  and scrolling include it.
- **Ctrl+PgUp = ascending, Ctrl+PgDn = descending**, handled in `OnKey` via the Ctrl-chord
  pattern; re-renders the Stream body in place. No collision (confirmed: nothing binds these).
- Shortcut reflected in the contextual help footer (`HelpItemSets.Detail`).
- Terminal.Gui 2.4.10 `Tabs`/`Dispose` teardown quirk (guarded in `CloseScreen`, `TodoApp.cs`)
  still works with the extra tab.

## Design decisions

- **`StreamSort` enum** (`Ascending` / `Descending`) in the formatter file — expressive and
  testable, better than a bare bool at the call site.
- **Block model.** Factor a private `JoinBlocks(blocks)` that joins with
  `"\n\n" + CommentSeparator + "\n\n"` (blank line, rule, blank line) and a `CommentBlock(c)`
  helper. `Comments()` is refactored to use them — verified byte-for-byte identical to the
  current output (existing `Comments_*` tests pin this), so no behavior change there. Stream
  reuses the same helpers so blocks look identical across the two tabs.
- **Description block** carries a `Description` header line (plus the task's created date when
  present, in the `X  ·  date` shape the comment headers use) so it reads as a peer block in the
  timeline, then the description text (or `(no description)`).
- **Undated comments = oldest.** Sort key is `DateMs ?? long.MinValue` ascending with an
  ordinal `Id` tiebreak for determinism; **descending is the exact reverse of ascending**
  (single sort to reason about, clean symmetry). This makes undated comments cluster at the
  oldest end — the same end as the Description — and matches FeedService's "nulls last in
  newest-first order" convention (#112). Documented in the method's XML doc.
- **Default sort = Ascending.** The issue's own phrasing — "lists the Description followed by
  the comments in order" — is the oldest-first reading (Description first, comments
  chronological). #S3 (#108) will make the default configurable.
- **Default tab = Stream.** Per the issue.

## Phases

1. **Formatter + tests.** Add `StreamSort`, `Stream(...)`, refactor `Comments()` onto shared
   block helpers. Unit tests for both directions, undated handling, separators, placeholders,
   and Comments-unchanged regression. Commit + push (opens draft PR).
2. **Screen wiring + help footer.** Insert the Stream tab as default, wire Ctrl+PgUp/PgDn to
   re-render, add the help item. Build; describe manual/TUI verification. Commit + push.
3. **Validate + finalize.** Full quality gate, `tui-validate` if it can drive the detail
   screen, `gh pr ready`, subagent review.

## Non-goals (deferred, tracked by sibling issues)

- Auto-scroll on toggle → **#107 (S2)**.
- Persisted default tab / default sort → **#108 (S3)**.
