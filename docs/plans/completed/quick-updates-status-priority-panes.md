# Quick Updates: Status & Priority panes — ✓ current value, apply-on-Enter (#157)

Part of the Quick Updates epic (#153). Sub-issue **D**. Depends on **C** (screen shell #156,
**closed**) and **A** (API facade #154, **closed** — `SetTaskPriorityAsync` already on
`IClickUpClient`). This issue fills in the **Status** and **Priority** panes of the existing
`QuickUpdatesScreen` shell so both are deferred-commit (edit does nothing until `Enter`), mark the
current effective value with a leading `✓`, and apply optimistically with revert-on-failure —
mirroring the existing `ApplyStatus` write path. The Assignees pane (immediate-apply + search) is
#158, untouched here.

## Goal / acceptance (from the issue)

1. Both lists show `✓` on the current effective value; changing the highlight **without** `Enter`
   does not write.
2. `Enter` commits the highlighted Status/Priority optimistically and reverts on server failure;
   an **unchanged** selection flashes "unchanged" and writes nothing.
3. The `✓` marker tracks the *effective* value, so after a successful commit the checkmark reflects
   the newly-applied value (the screen stays open — Esc exits, per the Epic).
4. Priority pane lists the four canonical priorities **plus** a "(no priority)" / clear option; the
   clear row commits `null` via `SetTaskPriorityAsync`.
5. Selector formatting + preselection + ✓-placement logic is unit-tested; `dotnet test` green;
   `tui-validate` confirms ✓ rendering and apply-on-Enter for both panes.

## Design decisions

- **Screen stays open on commit (Epic UX).** The shell's Enter-selects-then-`Close()` +
  `Chosen`-read-on-close pattern (a single-shot picker) is replaced by **live commit events**. Enter
  in the Status/Priority pane moves the `✓` optimistically and raises `StatusCommitted` /
  `PriorityCommitted`; the host applies (optimistic row + off-thread write + revert) and reconciles
  the pane's `✓` from the server-confirmed value. `Esc` exits. This is what makes "change three
  attributes without leaving your place" real and what makes "✓ reflects the newly-applied value"
  observable.
- **Host owns the write; pane owns its marker.** The host reuses the exact optimistic +
  off-thread-write + revert-on-failure shape from `ApplyStatus`. It looks the task up fresh from
  `_all` by id at commit time (not a stale captured record), so consecutive edits compose. On
  confirm/revert it calls back `screen.SetEffectiveStatus/Priority(...)` (guarded on the screen still
  being mounted) so the `✓` always reflects the server truth.
- **Pure marker logic in `QuickUpdatesModel`** (mirrors `StatusPickerModel`), fully unit-testable
  without a terminal: `Mark(label, current)` (`"✓ "` vs `"  "`, 2-col aligned); `StatusRows(statuses,
  effective)`; `PriorityRows(effectiveLevel)` (5 rows incl. `"(no priority)"`); `PriorityLevelForRow`
  / `PriorityRowForLevel` (row↔level incl. the clear row). The old stub helpers (`FormatPriority`,
  no-arg `PriorityRows`, `PreselectedPriorityIndex`) are superseded and removed with their tests.
- **Canonical priority colour is domain-owned.** Add `ClickUpPriority.ColorFromLevel(int?)` (the
  fixed ClickUp per-level colours) as the single source of truth and have `GroupHeaderPalette` reuse
  it (drops its private duplicate). The optimistic priority row uses it so the badge colour matches
  the level immediately; a `null` level clears the colour.
- **`_all` stays in sync per-field.** `UpdateTaskRow` already threads `StatusName` via
  `ApplyStatusChange`; add a symmetric `TaskService.ApplyPriorityChange(tasks, id, level, name,
  color)` and call it too. Because the passed `updated` record carries the *current* value for the
  unchanged field, applying both is always a no-op for the field that didn't change — no clobber.

## Hard-rule checkpoints

- No `Generated/` edits, no curated-spec / Kiota regen — the facade (`SetTaskPriorityAsync`) already
  exists from #154; no new ClickUp API surface.
- **No second focusable pane on the main dashboard (#3):** this is a modal screen; the list stays a
  single sectioned `ListView`. No new bare-letter keybindings (#12) — inside the screen only
  Tab/Shift+Tab/↑/↓/Enter/Esc/F1.
- Personal-token raw `Authorization` header untouched.
- Integration tests remain `SkippableFact`; the facade write itself was covered under #154.

## Phases

1. **Implementation + unit tests.** Domain colour + palette reuse; `QuickUpdatesModel` marker API;
   `QuickUpdatesScreen` apply-on-Enter + events + reconcile; `TodoApp` `ApplyStatus`/`ApplyPriority`
   (optimistic + revert + marker reconcile), `TaskService.SetPriorityAsync` + `ApplyPriorityChange`,
   `UpdateTaskRow` priority; help text. Unit tests for every pure bit. `dotnet build` +
   `dotnet test` green → first push, draft PR.
2. **Validate + finalize.** `tui-validate` (open → Tab to Priority → ↑/↓ → Enter applies + ✓ moves →
   Esc), `dotnet format`, mark PR ready, subagent review.

## Deferred (linked from the PR)

- Assignees pane search box + selector + immediate apply → **#158** (candidate pool → #155).
- Launch from Task Detail / return-to-origin → **#159**; updates on non-assigned tasks → **#160**.
