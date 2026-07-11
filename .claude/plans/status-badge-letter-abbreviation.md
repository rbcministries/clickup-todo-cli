# Status badge short variant: parenthesised letter abbreviation (#181)

## Goal

In `BadgeDisplay.Icons` mode the Status badge is currently a colour-only `" ○ "` chip
(`TaskRowFormatter.StatusIcon`, 3 columns). Users can't recall every status→colour
mapping, so replace it with a **4-column parenthesised letter abbreviation** of the
status name (`(WD)`, `(B )`, `(IP)`), still tinted the status colour. Priority's `⚑`
chip and `Text`/`Hidden` modes are unchanged.

## Abbreviation rules (from the issue)

- Word separators: `/`, `-`, `|`, whitespace. Other punctuation (apostrophes) does **not**
  split — `"Won't Do"` → words `Won't` / `Do`.
- Multi-word (≥2 words): first char of first word + first char of last word → `"Won't Do"` → `WD`.
- Single word: first letter + a space → `"Blocked"` → `B ` → `(B )`.
- Uppercase the extracted letters (issue's recommended decision) so lowercase ClickUp
  statuses stay consistent: `"in progress"` → `IP`.
- Always exactly 4 columns: `"(" + 2 chars + ")"`. No flanking padding.
- Defensive: a name that is *all separators* (no words) yields `(  )` (two spaces) so the
  4-column invariant always holds and no colour span is malformed.

## Rendering / alignment

- The coloured `StatusStart`/`StatusLength` span covers the full 4-char `(XX)`, so
  `StatusBadgeListSource` tints all four cells (unchanged wiring — `TryCreate` +
  `task.StatusColor` in `TodoApp.BuildRow`).
- The absent-Status alignment gutter widens to **4 columns** (`StatusGutter = "    "`),
  Status-specific — Priority's chip and its `BlankGutter` stay 3 columns.
- Grouped-by-Status still drops the badge entirely (no chip, no gutter), so alignment
  holds across grouped rows.

## Changes

### `src/ClickUpTodo/Tui/TaskRowFormatter.cs`

- Remove the now-dead `StatusIcon` const (`" ○ "` no longer rendered in icon mode).
- Add `StatusGutter = "    "` (4 cols) for the absent-status icon-mode gutter.
- Add pure public helper `StatusAbbreviation(string statusName)` → `(XX)`, unit-testable
  independent of Terminal.Gui.
- Icons case: append the abbreviation for a present status (span over the 4 chars) via a
  small `AppendStatusChip`; append `StatusGutter` when absent-but-not-grouped; drop
  entirely when grouped-by-status. Priority keeps `AppendIconChip`/`BlankGutter`.

### Doc cref fixups (build is 0-warning; dangling crefs would break it)

- `StatusPriorityBadge.cs` — the "icon-mode chips" doc referenced `TaskRowFormatter.StatusIcon`;
  point it at `PriorityIcon` (the glyph is still the icon-mode Priority chip and the
  Text/detail label) since Status no longer renders the glyph in icon mode.
- `FeedRowFormatter.cs` — `MentionChip` "same width as … StatusIcon" cref → `PriorityIcon`
  (still the 3-column icon chip; the status chip is now 4 columns).

## Tests

### `tests/ClickUpTodo.Tests/TaskRowFormatterTests.cs`

- New `StatusAbbreviation` theory covering the AC examples: `"Won't Do"`→`(WD)`,
  `"Blocked"`→`(B )`, `"In Progress"`→`(IP)`, `/`-`-`-`|`-separated splits, apostrophe
  doesn't split, lowercase→uppercase, all-separators→`(  )`, always 4 columns.
- Update Icons-mode tests that assert the literal `StatusIcon` chip to assert the
  abbreviation of the row's status (e.g. `"to do"`→`(TD)`) and the 4-col `StatusGutter`.
- Update `IconMode_ChipsAndBlankGutter_AreTheSameWidth`: Priority chip == its `BlankGutter`
  (3); Status chip (abbrev) == `StatusGutter` (4).

### `tests/ClickUpTodo.Tests/StatusBadgeListSourceTests.cs`

- `IconChips_And_BlankGutter_OccupyTheSameDisplayColumns`: priority/blank stay 3 cols; the
  status abbrev chip + `StatusGutter` are 4 cols. The `StatusChip_ColumnsMatchBaseRenderer`
  / `PriorityChip_FollowingStatusChip_ColumnsMatchBaseRenderer` column-math invariants hold
  unchanged (ASCII `(XX)`: char length == display columns).

## Validation

- `dotnet build -c Release` (0 warn/0 err) + `dotnet test -c Release` green.
- `tui-validate` (PTY + pyte) to confirm the abbreviation renders in the status colour and
  titles stay aligned — run only after `dotnet test` is green.
