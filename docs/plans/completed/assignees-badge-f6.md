# Trailing assignees badge (👥) folded into the F6 cycle — #161

Part of #153 (Quick Updates epic). Builds on the shipped F6 badge infrastructure (#151).
Independent of the Quick Updates screen work and of the in-flight dispatch / feed PRs.

## Goal

Give the main task list a **trailing** badge for tasks that have an assignee **other than the
current user**, so shared/delegated work is visible at a glance. White background, 👥 glyph in Icons
mode, assignee names in Text mode, folded into the existing F6 `BadgeDisplay` cycle so one keypress
moves all badges together.

## Verified current state

- Row text + coloured badge spans are built by `TaskRowFormatter.Format` (pure), which returns a
  `Row(Text, StatusStart, StatusLength, PriorityStart, PriorityLength)`. `TodoApp.BuildRow` turns the
  status/priority spans into `StatusBadgeListSource.Badge`s via `StatusBadgeListSource.TryCreate`,
  which tints the span with the field's ClickUp hex colour and picks a readable fg via
  `StatusBadgeColor.PreferDarkText`.
- `BadgeDisplay` (Icons → Text → Hidden) is cycled by F6 and persisted in `config.json` (#151). The
  leading Status/Priority badges render per the active mode inside a `switch (badges)` in `Format`.
- `TaskItem.Assignees` (`IReadOnlyList<TaskAssignee>`, each `Id`+`Name`) carries per-row assignees;
  `TaskService.UserId` (`long`) is the current user's ClickUp id. `TodoApp` holds `_tasks`.
- Trailing context markers (`· (parent — not assigned to you)` / `· (not assigned to you)`) are
  appended at the very end of the row text.

## Design

Reuse the existing colour-span machinery end-to-end; no change to `StatusBadgeListSource` or
`BadgeDisplay`.

### `TaskRowFormatter` (Tui/TaskRowFormatter.cs)

- Extend `Row` with `AssigneesStart, AssigneesLength` (positional record → 7 members). Only `Format`
  constructs `Row`, so this is contained.
- Add `long? currentUserId = null` as the trailing optional param of `Format` (appended after
  `badges`, so every existing named-arg call still compiles).
- Compute `others = task.Assignees.Where(a => currentUserId is not { } uid || a.Id != uid)` — when
  the current user is unknown (null), every assignee counts as "other". Badge shows when
  `others` is non-empty **and** the list isn't grouped by Assignee (`groupedBy != TaskField.Assignee`
  — the #67 rule: a group header for the field already conveys it) **and** the mode isn't Hidden.
- New `public const string AssigneesIcon = " 👥 "` (space-glyph-space, matching the padded-chip
  style of `StatusIcon`/`PriorityIcon`; a single emoji chip whose white background reads as a chip).
- New `AppendAssigneesBadge(ref text, badges, show, others)`: appends a 2-space separator (uncoloured,
  matching the `  · …` trailing segments) then a white-background chip — `AssigneesIcon` in Icons
  mode, `" {name, name} "` in Text mode — and returns the coloured span of the chip (padding
  included). Appended **after** the list/due segments and **before** the context/foreign markers so
  the span offsets stay exact and the markers still read last. Hidden ⇒ no append, `(-1, 0)`.

### `TodoApp` (Tui/TodoApp.cs)

- `BuildRow` gains a `long currentUserId` param, passes it to `Format`, and adds the assignees badge
  via `StatusBadgeListSource.TryCreate(row.AssigneesStart, row.AssigneesLength, "ffffff")` — white bg,
  black fg (via `PreferDarkText`). Absent/hidden ⇒ span `(-1, 0)` ⇒ `TryCreate` returns null ⇒ no
  shading, exactly like status/priority.
- `AddTask` and `UpdateTaskRow` pass `_tasks.UserId`.

## Tests (Phase 1, test-first)

`TaskRowFormatterTests` additions:

- Icons mode: a task with a non-current-user assignee gets a trailing `AssigneesIcon` chip; the
  reported `Assignees*` span lands exactly on it; solo (only current user) and unassigned tasks get
  no chip and report `(-1, 0)`.
- Text mode: trailing white chip lists the other assignees' names (comma-joined); span exact.
- Hidden mode: no chip, `(-1, 0)`, no 👥 in the text.
- Current-user filtering: mixed [me + teammate] shows the badge listing only the teammate; [me only]
  shows nothing; null currentUserId ⇒ all assignees count.
- Grouped-by-Assignee drops the badge (parity with status/priority #67).
- Placement: the chip precedes the `(not assigned to you)` / `(parent — …)` markers and follows the
  `· list` / `· due` segments; the status/priority spans are unaffected by the new trailing badge.

## Phases

1. **Formatter + tests** — `Row`, `Format`, `AppendAssigneesBadge`, `AssigneesIcon`; unit tests.
   Build + test green + `dotnet format`. Commit, push → opens draft PR.
2. **Wiring + TUI validation** — `BuildRow`/`AddTask`/`UpdateTaskRow` threading; build + test;
   `tui-validate` PTY pass to confirm the white bg, 👥 glyph, names variant, and all three F6 states;
   describe manual verification in the PR; mark ready.

## Hard-rule check

- No `Generated/` edit, no curated-spec change, no Kiota regen (pure TUI/formatter change).
- Personal-token raw `Authorization` header untouched.
- New tests are pure xUnit units; no integration boundary added.
- No second focusable pane — the badge is text within the existing single sectioned `ListView`.
- Bare letters stay reserved for type-ahead; no new keybinding (F6 already cycles).
