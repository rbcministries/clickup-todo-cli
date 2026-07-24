# Shared TUI helpers (de-dup TodoApp ↔ SingleTaskApp)

Issue: [#346](https://github.com/rbcministries/clickup-todo-cli/issues/346).

## Problem

`Tui/SingleTaskApp` (single-task launch mode, #296/#344) intentionally hosts one task's
`TaskDetailScreen` in isolation from `Tui/TodoApp` rather than teaching `TodoApp` a "no-list root"
mode (which would bend the load-bearing `ShowScreen`/`CloseScreen` seam behind the single-`ListView`
invariant #3/#38). Correct call for #296's blast radius — but it left several **near-copies** of
`TodoApp` members in `SingleTaskApp`, a maintenance-drift risk that had **already drifted once**
(`LaunchBrowser`, fixed in #344).

## Scope of this PR (narrowed)

This PR extracts the **churn-free** duplicated members — the ones that do **not** sit on the
`_screens` screen-stack / navigation seam — into single-source components:

- **`ErrorText.Short(Exception)`** — the status-line exception formatter.
- **`TuiTeardown.DisposeSwallowingTeardownBug`** — the Terminal.Gui 2.4.10 tabbed-view dispose-bug
  swallow, previously copied into both `Run` finallys.
- **`ClickUpTaskBrowser.Open`** — the app.clickup.com → workspace-subdomain rewrite (#304) + parse +
  launch core (the member that already drifted, per #346), returning an outcome so each host formats
  its own message.
- **`ContextualFooter`** — the status + contextual-help rows: `Flash`, `Status`/`CommitStatus`, and the
  column-aware `RenderHelp` (returning the fitted set so `TodoApp` keeps its #289 footer-click
  hit-test). The help-item *source* and status wording stay with each host.

Both hosts delegate; behaviour is unchanged. The extracted helpers are unit-tested where extractable.

## Deferred: the shared screen-stack host → #402

The screen-stack seam itself (`ShowScreen`/`CloseScreen`/`ActiveScreen` → a shared `ScreenStackHost`)
is **not** in this PR. That seam is being actively reworked on `main` by the multi-tab navigation
effort — #401 (`NavigationHistory` model + `RequestExit`), #291/#373 (Task Tree detail→detail),
#387 (Ctrl+O quick-open detail→detail), and #403 (wire `NavigationHistory` as the logical back-stack).
[#402](https://github.com/rbcministries/clickup-todo-cli/issues/402) explicitly folds the shared
`ScreenStackHost` extraction into its screen-navigation reconciliation, so the host should wrap the
*settled* seam (after #403 decides the `NavigationHistory`↔`_screens` contract) rather than a snapshot
of a moving one. The `ScreenStackHost` design from this branch's history is carried forward there.

## Constraints

- **No behaviour change** to either host: tui-validate A/B renders stay byte-identical.
- **Single-`ListView` invariant (#3/#38):** no second focusable pane. The footer's help label stays
  `CanFocus=false`.
- **Terminal.Gui is not unit-testable in CI** (repo convention): the footer extraction is verified by
  build + tui-validate; `ErrorText` and `ClickUpTaskBrowser` are pure and unit-tested.

## Acceptance criteria (from #346, partial)

- The duplicated `Short`, teardown guard, browser-launch, and footer members exist in one place.
- `dotnet test` green; the extractable logic is unit-tested.
- No behaviour change (tui-validate A/B byte-identical; single-task launch unaffected).

Does **not** close #346 — the shared `ScreenStackHost` (the issue's headline) is deferred to #402.
