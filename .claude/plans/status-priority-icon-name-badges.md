# Status/Priority badges as `{icon} {name}` — detail title line + unified list text badges (#162)

## Goal (from the issue)

Surface Status and Priority as coloured **`{icon} {state name}`** badges (`○ In Progress`,
`⚑ Urgent`) in two places, backed by **one shared formatter**:

1. **Task Detail title line** — append trailing `○ {status}` / `⚑ {priority}` badges after
   `"{Name}  ({CustomId})"`, in the field's ClickUp colour with a readable foreground. The header is
   plain text today, so it needs a colour-span rendering path.
2. **Main-list `Text` badge mode** — replace the `[status]` / `[priority]` bracket format in
   `TaskRowFormatter.AppendTextBadge` with the same shared `{icon} {name}` badge, colour spans
   unchanged in extent. `Icons` and `Hidden` modes are untouched.

Absent status/priority → no badge, consistently in both surfaces.

## Verified current state

- **Detail header** (`TaskDetailFormatter.Header`, `TaskDetailFormatter.cs:17`) builds a plain
  multi-line string (`"{Name}  ({CustomId})\nTags: …\nAssignees: …"`). Rendered as a plain `Label`
  in `TaskDetailScreen.cs:133-142`. No colour today.
- **Colour-span machinery already exists:** `TaskDetailFormatter.DetailRun(Text, Color?)` /
  `DetailLine(Runs)` (`:125-132`) + `DetailAttributesView` (`DetailAttributesView.cs`) render per-run
  hex colours as badges via `StatusBadgeListSource.HeaderAttr(hex)` (bg = colour, fg =
  `StatusBadgeColor.PreferDarkText`). Used today for the Other tab's Priority/Status values (#66).
- **List text badges:** `TaskRowFormatter.AppendTextBadge` (`TaskRowFormatter.cs:196`) emits
  `"[label] "`, reporting the `[label]` char span. The bracket span is `2 + label.Length` chars.
  The `{icon} {name}` form is `glyph(1) + space(1) + label.Length` = **also `2 + label.Length`** →
  span extent is unchanged. The span is tinted by `TodoApp.BuildRow` via
  `StatusBadgeListSource.TryCreate(start, length, StatusColor/PriorityColor)` — mode-agnostic, so no
  `TodoApp`/list-renderer change is needed.
- **Icons:** `TaskRowFormatter.StatusIcon = " ○ "`, `PriorityIcon = " ⚑ "` — single-column glyphs.

## Design

### 1. Shared formatter (`StatusPriorityBadge.cs`, new, pure)

Single source of the badge glyphs + `{icon} {name}` text, reused by both surfaces so the label can't
drift:

```csharp
public static class StatusPriorityBadge
{
    public const string StatusGlyph = "○";
    public const string PriorityGlyph = "⚑";
    public static string Status(string name)   => $"{StatusGlyph} {name}";
    public static string Priority(string name) => $"{PriorityGlyph} {name}";
}
```

`TaskRowFormatter.StatusIcon`/`PriorityIcon` become `$" {StatusPriorityBadge.StatusGlyph} "` etc.
(constant interpolated strings; single source of the glyph). Byte-identical to today (`" ○ "`/`" ⚑ "`).

### 2. List text badges (`TaskRowFormatter.AppendTextBadge`)

Add a `glyph` parameter; badge text becomes `$"{glyph} {label}"` instead of `$"[{label}]"`. Callers
pass `StatusPriorityBadge.StatusGlyph` / `PriorityGlyph`. Span = whole badge token (excludes the
trailing separator space), exactly as before. Icons/Hidden paths unchanged.

### 3. Detail title line (`TaskDetailFormatter.HeaderLines` + `TaskDetailScreen`)

- New `HeaderLines(TaskDetail) : IReadOnlyList<DetailLine>` — the structured header:
  - **Line 1:** `DetailRun(title)` where `title = Name (+ "  (CustomId)")`; then, when present,
    `DetailRun("  ")` + `DetailRun(StatusPriorityBadge.Status(StatusName), StatusColor)`; then, when
    present, `DetailRun("  ")` + `DetailRun(StatusPriorityBadge.Priority(Priority), PriorityColor)`.
    Absent field → neither its separator nor its badge run is emitted.
  - **Tags line** (when non-empty) and **Assignees line** — single uncoloured runs, same text as today.
- Reimplement `Header(TaskDetail)` as `string.Join("\n", HeaderLines(task).Select(l => l.Text))` so
  the plain-text form stays a single source of truth (now includes the badge labels on line 1).
- `TaskDetailScreen`: replace the plain header `Label` with a `DetailAttributesView(HeaderLines(task))`
  sized to `HeaderLines.Count` (mirrors the existing Other-tab usage), positioned where the Label was
  (`X=1, Y=0, Width=Dim.Fill(1)`). No new focusable pane (the view is `CanFocus=false`, like the Label).

## Tests

- **`StatusPriorityBadgeTests`** (new): glyph constants; `Status`/`Priority` produce `○ …` / `⚑ …`.
- **`TaskRowFormatterTests`** (update text-mode cases): `○ to do` / `⚑ High` replace `[to do]`/`[High]`;
  spans still cover the badge token and exclude the separator; status-before-priority ordering;
  no-status ragged case; indented case; grouped-away cases (`DoesNotContain "○ in progress"`); literal
  `[High]` in a title no longer needs the bracket-confusion guard but keep an equivalent guard for the
  glyph badge. Icons/Hidden assertions unchanged.
- **`TaskDetailFormatterTests`** (update `Header_*` + add `HeaderLines_*`): title still leads with the
  name; custom id still present; badge runs carry `○ {status}` / `⚑ {priority}` with the right
  `Color`; absent status/priority → no badge run; tags/assignees lines unchanged.

## Verification

- `dotnet build -c Release` (0/0), `dotnet test -c Release` (integration self-skips), `dotnet format`.
- `tui-validate`: detail-header badges + list text-mode badges render with correct glyph + colour;
  no latency/output-volume regression on the shared dashboard path.

## Out of scope / not changed

- `Icons` and `Hidden` badge modes; the list colour-overlay renderer (`StatusBadgeListSource`,
  `TodoApp.BuildRow`); the Assignees badge (#161); grouping behaviour (#67).
