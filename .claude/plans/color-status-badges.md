# Plan — Color status badges by ClickUp status color (issue #16)

## Goal

Render each task's `[status]` span (brackets included) with a background matching the
ClickUp status color, and a foreground (black/white) chosen for contrast. Color only the
`[status]` span, not the whole row.

## Acceptance criteria (from #16)

1. **Background** = the status' hex color (`TaskItem.StatusColor`, e.g. `#87909e`),
   mapped to 24-bit TrueColor; nearest 16/256 fallback on limited terminals.
2. **Foreground contrast** = black or white chosen from the background's relative
   luminance (WCAG `L = 0.2126R + 0.7152G + 0.0722B` on linearized channels).
3. **Scope** = only the `[status]` span (brackets + name), not the whole row.

We already have the data: `TaskItem.StatusColor` is populated from the API `Status.color`.

## Design

The bulk of the work is per-substring coloring inside a `ListView`, which renders plain
strings. The cleanest, lowest-risk Terminal.Gui v2 hook is a **custom `IListDataSource`
that composes the stock `ListWrapper<string>`**:

- The custom source delegates *everything* — `Count`, `MaxItemLength`, `ToList`
  (which feeds the type-ahead `CollectionNavigator`, so #12 keeps working), marking,
  `CollectionChanged`, and the actual text drawing — to an inner `ListWrapper<string>`
  built over the same `ObservableCollection<string>` used today.
- After the inner renderer draws a row's text, the custom `Render` **overlays** the
  status color: it re-draws just the cells of the `[status]` span with an `Attribute`
  whose background is the status color and whose foreground is the chosen contrast color.

Why this shape: text layout, horizontal scroll, wide-rune handling, selection highlight,
and type-ahead are all inherited unchanged from the proven `ListWrapper`. The only new
behavior is a color overlay on a known character span. Worst-case failure mode is a
mis-placed color cell (e.g. exotic wide titles while horizontally scrolled), never
garbled or missing text — strictly safer than re-implementing the renderer.

TrueColor → 16/256 downgrade is handled by Terminal.Gui's driver/color quantization; we
construct a 24-bit `Color` and let the active driver map it to the terminal's capability.

### New files

- `src/ClickUpTodo/Tui/StatusBadgeColor.cs` — **pure, no Terminal.Gui dependency**, unit-tested:
  - `TryParseHex(string? hex, out int r, out int g, out int b)` — `#rrggbb` / `rrggbb` / `#rgb`.
  - `RelativeLuminance(r,g,b)` — WCAG linearized luminance in [0,1].
  - `ContrastRatio(...)` — WCAG contrast ratio between two colors.
  - `PreferDarkText(r,g,b)` — true ⇒ black foreground, false ⇒ white, whichever has the
    higher contrast against the background.
- `src/ClickUpTodo/Tui/TaskRowFormatter.cs` — **pure**, unit-tested:
  - `Format(TaskItem) -> (string Text, int BadgeStart, int BadgeLength)` — the display
    line (title-leading, so type-ahead matches titles) plus the char span of `[status]`
    (`BadgeLength == 0` when the task has no status). Replaces `TodoApp.Format`.
- `src/ClickUpTodo/Tui/StatusBadgeListSource.cs` — `IListDataSource` composing
  `ListWrapper<string>` + a parallel `List<Badge?>`; `Render` overlays the badge attribute
  on the `[status]` span (column-space positioning via `Rune.GetColumns`, honoring
  `viewportX`). `Badge.TryCreate(start,len,hex)` builds the `Attribute` (null if no/invalid color).

### `TodoApp` changes

- Keep `_display` (`ObservableCollection<string>`) and add a parallel `List<Badge?> _badges`;
  build a `StatusBadgeListSource` over both and assign `_list.Source` (replacing `SetSource`).
- `AddTask`/`AddHeader` populate `_badges` alongside `_display` (header rows ⇒ `null`).
- `UpdateTaskRow` (the #11 in-place path) updates `_display[i]` **and** `_badges[i]`; the
  inner wrapper's `CollectionChanged` (forwarded) triggers the targeted redraw as before.
- `Format` delegates to `TaskRowFormatter`; the optimistic `(sending…)` suffix is appended
  after the formatted text, leaving the `[status]` span (and thus its color) intact.

### Hard-rule compliance

- No generated-client edits; no OpenAPI/Kiota change (data already mapped to `StatusColor`).
- No second focusable pane; still one sectioned `ListView`. The overlay is strictly less
  work than the old full reload, so it can't regress input latency (#3).
- Bare letters still reserved for type-ahead (#12) — preserved via `ToList()` delegation.

## Tests (xUnit, no UI)

`tests/ClickUpTodo.Tests/StatusBadgeColorTests.cs`:
- `TryParseHex`: `#87909e`, `87909e`, `#fff`, and rejects null/empty/`#xyz`/wrong length.
- `RelativeLuminance`: white ⇒ 1.0, black ⇒ 0.0 (within tolerance); monotonic.
- `PreferDarkText`: white/yellow bg ⇒ dark text; black/navy bg ⇒ light text; ClickUp's
  default grey `#87909e` ⇒ light text.

`tests/ClickUpTodo.Tests/TaskRowFormatterTests.cs`:
- Text contains title, `[status]`, list, due in order; title leads.
- `BadgeStart/Length` exactly span `[status]`; substring at the span equals `[{status}]`.
- No status ⇒ `BadgeLength == 0` and no `[` in the (status-less) prefix.

The custom `IListDataSource` overlay needs a live driver, so it is **build-verified +
reasoned**, not unit-tested (consistent with the repo's TUI testing rule). Manual
verification steps below.

## Manual TUI verification (CI can't drive Terminal.Gui)

`dotnet run --project src/ClickUpTodo`:
- Each `[status]` badge shows its ClickUp color as background with readable (black/white)
  text; the rest of the row is uncolored.
- Cursor movement / Tab / type-ahead unchanged; no input lag.
- On a truecolor terminal the exact hex shows; on a 16-color terminal a nearest color shows.

## Phases

1. Plan + pure logic (`StatusBadgeColor`, `TaskRowFormatter`) + unit tests.
2. Custom `IListDataSource` + `TodoApp` wiring.
3. Quality gate (build 0-warn, tests green, format), subagent review, finalize.
