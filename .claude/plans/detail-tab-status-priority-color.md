# Plan — Colour Status/Priority in the detail Other-attributes tab (issue #66)

Follow-up / deferred from #55 (PR #65). #55 added a coloured `[priority]` badge to the **list row**
by generalising the #16 `StatusBadgeListSource` overlay, but deliberately did **not** colour
Priority (or Status) in the **detail view's Other tab** (#17), because that tab renders into a plain,
read-only Terminal.Gui `TextView` with no per-span colour mechanism. This issue adds that machinery.

## Acceptance criteria (from #66)

1. Decide the rendering approach for coloured runs inside the detail Other tab (attribute-run-aware
   `TextView` subclass, or a dedicated small view for the header attributes).
2. Add `PriorityColor` to `TaskDetail` and map it in `ClickUpClient.MapDetail` (mirrors the list-row
   `TaskItem.PriorityColor`).
3. Colour the `Priority:` and (for consistency) `Status:` values using `StatusBadgeColor`, keeping
   the colour→attribute mapping in the existing pure helper.
4. Verify visually (Terminal.Gui isn't unit-testable in CI), per the repo's TUI rule.

## Design decisions

### Rendering approach — a dedicated small colour view above a scrollable body (AC #1)

The Other tab today is a single `TextView` holding one string (header attributes + a blank line +
the "Custom fields:" section). `TextView` cannot colour individual spans, and word-wrap makes an
overlay (#63-style column math) fragile. So the tab content becomes a **container** holding:

- **`DetailAttributesView`** (new, custom `View`) at the top — draws the fixed header attribute lines
  (List / Lists / Priority / Status / Created / Last activity / Due) with **per-run colour**. Height =
  exactly the number of header lines; **not focusable**, **no scrolling** (the header is small and
  always fits). This is the "dedicated small view for the header attributes" option the issue offers.
- The existing **`TextView`** below it (Y = headerLines + 1, leaving a blank gap row) for the
  **"Custom fields:" body** — unchanged: read-only, word-wrapped, and **still scrollable** (↑/↓/PgUp/
  PgDn). No custom scrolling code, so no regression risk for long custom-field lists.

`DetailAttributesView.OnDrawingContent` draws each line by `Move(0, row)` then, per run,
`SetAttribute(attr)` + `AddStr(run.Text)` — consecutive `AddStr` calls flow at the driver's own
width, so no hand-rolled column math (avoids the #63 failure mode). Uncoloured runs use the view's
current (normal) attribute; a coloured run uses `StatusBadgeListSource.HeaderAttr(hex)` (fg = the
higher-contrast of black/white via `StatusBadgeColor.PreferDarkText`, bg = the hex colour) — the
**same** badge look and the **same pure colour helper** as the list row (AC #3), so nothing new in the
colour math.

### Pure layer — structured header lines with text parity (AC #2, #3)

`TaskDetailFormatter` gains a Terminal.Gui-free structured representation (colours as hex strings):

- `readonly record struct DetailRun(string Text, string? Color)`
- `sealed record DetailLine(IReadOnlyList<DetailRun> Runs)` with `Text => concat(Runs.Text)`
- `HeaderAttributeLines(TaskDetail)` → the List..Due lines; the Priority value run carries
  `task.PriorityColor` and the Status value run carries `task.StatusColor` (null when the value is the
  em-dash placeholder), every other run uncoloured.
- `CustomFieldsBody(TaskDetail)` → the "Custom fields:" section string (as today).

`OtherAttributes(TaskDetail)` is refactored to compose from `HeaderAttributeLines` + a blank line +
`CustomFieldsBody`, producing **byte-identical** output to today's — so every existing
`OtherAttributes` test assertion (exact label spacing, dashes, multi-list line, custom-field values)
keeps passing. This guarantees the string tab-body path and the structured colour path can't drift.

### Model + mapping (AC #2)

- `Models.cs`: add `public string? PriorityColor { get; init; }` to `TaskDetail` (mirrors
  `TaskItem.PriorityColor` and the existing `TaskDetail.StatusColor`).
- `ClickUpClient.MapDetail`: `PriorityColor = t.Priority?.Color` (the generated `Priority.Color`
  already exists — used by `Map` — so **no curated-spec / Kiota regen needed**). `MapDetail` becomes
  `internal` (mirroring `Map`, with the same "so the mapping can be unit-tested" note) for a mapping test.

## Tests (xUnit, no UI)

- `ClickUpClientMapTests`: `MapDetail` carries `StatusColor` + `PriorityColor` from the generated
  `Status`/`Priority`.
- `TaskDetailFormatterTests`:
  - `HeaderAttributeLines` colours the Priority value with `PriorityColor` and the Status value with
    `StatusColor`, and leaves the labels + other lines uncoloured.
  - The value run's colour is null when Priority/Status is absent (em-dash).
  - Parity: `OtherAttributes(task)` equals the header lines' text + blank line + `CustomFieldsBody`
    (guards the refactor against drift). All existing `OtherAttributes` assertions unchanged.

## TUI verification (per repo rule — not unit-testable in CI)

Build succeeds (0/0). No new focusable pane in the **dashboard** (input-latency regression #3 is about
the dashboard's single sectioned `ListView`; this change is entirely inside the already-multi-view
detail screen). No keybinding changes. Manual check: open a task's detail, Tab to **Other** — the
`Priority:` and `Status:` values render as coloured badges matching the list row; custom fields still
scroll with ↑/↓.
