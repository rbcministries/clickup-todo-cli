# Quick Updates: screen shell — rename, Tab navigation, Esc exit (#156)

Part of the Quick Updates epic (#153). Sub-issue **C**. Depends on **A** (API facade #154,
**closed/merged** — `SetTaskPriorityAsync` / `Add`/`RemoveTaskAssigneeAsync` already exist on
`IClickUpClient`). Panes' apply behaviour lands in the follow-ups: Status/Priority apply-on-Enter
(#157), Assignees search + immediate apply (#158). This issue only needs the three panes **present
and focusable** with `Tab`/`Shift+Tab` cycling and `Esc` exit.

## Goal / acceptance (from the issue)

1. `Space` opens a **Quick Updates** screen titled `Quick Updates — {task}`, hosting three
   `Tab`-navigable controls: **Status → Priority → Assignees**.
2. `Tab` / `Shift+Tab` cycles focus Status → Priority → Assignees and **wraps**.
3. `Esc` exits back to the main list from **every** pane.
4. The existing open-from-cache fast-path (`TryGetCachedStatuses`) and cold-path (off-thread fetch +
   `Application.Invoke`) are preserved, as are the context-parent / foreign-subtask / no-list guard
   `Flash`es (#46 / #70).
5. Help: replace `HelpItemSets.StatusPicker` with a **Quick Updates** set (Tab switch control, Esc
   exit, per-pane hints); update `HelpItemSets` references + `HelpScreen`.
6. `dotnet test` green; `tui-validate` confirms open → Tab across the three panes → Esc exit.

## Design decisions

- **No regression on Status.** The old `StatusPickerScreen` behaviour (open from warmed cache,
  Enter-selects-a-status, host applies optimistically via `ApplyStatus`, revert-on-failure) is
  preserved as the **Status pane's** behaviour so `main` never regresses between #156 and #157.
  `QuickUpdatesScreen.Chosen` keeps exposing the picked status name; `TodoApp` reads it in the close
  handler exactly as today.
- **Priority + Assignees are minimal stubs.** Priority is a focusable `ListView` of the canonical
  `ClickUpPriority.Names` (Urgent/High/Normal/Low), pre-selecting the task's current level; Enter is a
  no-op here (apply lands in #157). Assignees is a focusable `ListView` showing the task's current
  assignees (or `(no assignees)`); the candidate-pool + search + immediate-apply is #158, and the
  frequency cache it draws on is #155 (**not yet merged**) — noted as deferred, not wired here.
- **Pure nav seam.** A new `QuickUpdatesModel` (mirrors `StatusPickerModel`) owns the unit-testable
  bits: pane cycle/wrap order, priority row formatting + preselect index, assignee row rendering.
  `StatusPickerModel` is kept as-is (status pane formatting/preselect) and still tested.
- **Single-focusable-pane invariant (#3) is about the main dashboard list**, not modal screens.
  Settings and Detail already host several `Tab`-navigable controls; this screen follows that
  precedent. The main list stays a single sectioned `ListView`.

## Hard-rule checkpoints

- No `Generated/` edits, no curated-spec / Kiota changes (no new API surface — facade already there).
- No new bare-letter keybindings (#12): Space opens it; inside, only Tab/Shift+Tab/Enter/Esc/F1.
- Personal-token raw `Authorization` header untouched.

## Phases

1. **Model + help sets (non-UI, fully unit-tested).** Add `QuickUpdatesModel` + `QuickUpdatesModelTests`;
   rename `HelpItemSets.StatusPicker` → `QuickUpdates` (new content) and update `HelpLineTests`
   references. `dotnet build` + `dotnet test` green. → first push, draft PR.
2. **Screen + wiring.** Replace `StatusPickerScreen` with `QuickUpdatesScreen`; rewire
   `TodoApp.OpenStatusPicker`/`ShowStatusPicker` → `OpenQuickUpdates`/`ShowQuickUpdates` (guards +
   fast/cold paths preserved); update `HelpScreen` body text. Build + reason.
3. **Validate + finalize.** `tui-validate` (open → Tab×3 → Esc), `dotnet format`, mark PR ready.

## Deferred (linked from the PR)

- Status/Priority apply-on-Enter with `✓` current-value marker → **#157**.
- Assignees search box + selector + immediate apply → **#158**; its candidate pool → **#155**.
