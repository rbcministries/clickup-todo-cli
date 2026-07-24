# Task Detail (C): render & style links in the text panes (#317)

Issue: [#317](https://github.com/rbcministries/clickup-todo-cli/issues/317) ·
Epic: [#313](https://github.com/rbcministries/clickup-todo-cli/issues/313)

Builds on **B** ([#316](https://github.com/rbcministries/clickup-todo-cli/issues/316),
the merged `TaskLinkExtractor` link model). Pairs with **D** (mouse click, #318) and
**E** (focus traversal, #319). This slice ships only the **visual styling** of the links
**B** already detects — no click or focus activation.

## Goal

Render the links `TaskLinkExtractor` finds with distinct styling in the Description /
Comments / Stream panes (`DetailPaneView`):

- **ClickUp task links** → the pane's default text colour, **underlined** (Terminal.Gui
  has no dedicated hyperlink `VisualRole`, so "the pane's link attribute" is expressed as
  the read-only foreground with an underline — an unobtrusive "this is a link" cue that
  still reads as body text).
- **Other web links** → **blue + underlined**, the way a terminal renders a bare URL.

## Verified current state

- The three body panes are all `DetailPaneView : TextView` (`_descriptionPane`,
  `_commentsPane`, `_streamPane` in `Tui/Screens/TaskDetailScreen.cs`), loaded via
  `DetailPaneView.SetBody` → the pure `BuildCells`, which splits the body into one
  `List<Cell>` per line and tags whole **separator** lines with `SeparatorMarker` (a
  `Color.None`-background sentinel). `OnDrawReadOnlyColor` re-resolves those tagged cells
  at draw time against the live `ReadOnly` role. This tag-then-redraw seam is exactly the
  hook link styling needs.
- `ReadOnly = true`, so the base `OnDrawReadOnlyColor` paints every cell in the `ReadOnly`
  role and ignores per-cell attributes — which is why separators need the draw override.
  Link cells need the same interception.
- `TaskLinkExtractor.Extract(text)` returns `LinkSpan { Start, Length, Kind, Url, TaskId }`
  with **char offsets** into the string passed in. URLs never span a `\n` (the regex stops
  at whitespace), so extracting **per line** yields per-line offsets directly — no
  global-offset → (line, col) mapping is needed.

## Design

All changes are in `Tui/DetailPaneView.cs` (+ its unit tests) and the E2E harness. The pure
`TaskLinkExtractor` (**B**) stays Terminal.Gui-free; styling lives only in the view.

### Tagging (pure, unit-tested) — `BuildCells`

For each body line:
1. A separator line → tagged with `SeparatorMarker` (unchanged).
2. Otherwise run `TaskLinkExtractor.Extract(line)`. With no links, the whole line is left
   untagged (null attribute → normal read-only colour), exactly as today.
3. With links, the line is assembled by **char-offset segments**: `[0,Start)` untagged,
   `[Start,End)` tagged with `TaskLinkMarker`/`WebLinkMarker` by `Kind`, repeat, then the
   tail. Each segment is turned into cells with `Cell.ToCellList(slice, marker)` and
   concatenated. Segment boundaries fall on link edges (whitespace/ASCII), never inside a
   grapheme cluster, so slicing by UTF-16 char index is safe.

Two new sentinel attributes, distinct from each other and from `SeparatorMarker` (both have
an **opaque** background, so they never trip the `Background == Color.None` separator
branch):

```csharp
public static readonly Attribute TaskLinkMarker = new(Gray,       Black, TextStyle.Underline);
public static readonly Attribute WebLinkMarker  = new(BrightBlue, Black, TextStyle.Underline);
```

Their concrete colours are placeholders — the draw override re-resolves the real,
theme-aware attribute — but they double as a sane fallback if a tag ever reached the screen
un-resolved.

A pure `ClassifyCell(Cell) -> {Normal, Separator, TaskLink, WebLink}` exposes the tagging
for unit tests (which run the real `Cell.ToCellList` round-trip, no driver needed).

### Drawing (Terminal.Gui glue) — `OnDrawReadOnlyColor`

A link cell (tag equals `TaskLinkMarker`/`WebLinkMarker`) is repainted keeping the live
`ReadOnly` **background** (so it sits in the pane exactly like surrounding text) with:
- web link → a fixed **blue** foreground + `Underline`;
- task link → the `ReadOnly` **foreground** + `Underline`.

The separator branch is unchanged and checked first.

### Word-wrap

Cells are tagged on the **logical** (pre-wrap) line; `TextView` word-wrap only splits a
logical line into more display rows, preserving each cell's attribute — so a link that wraps
keeps its styling contiguous with no extra work (asserted indirectly by the existing
separator confinement test's word-wrap reasoning).

## Scope boundary / deferred

- **Underline-on-hover** for web links (issue's "underline on mouse-over"): the hover state
  comes from **D**'s (#318) mouse-position handling, which isn't landed. Per the issue's
  "degrade gracefully to always-underlined if hover is unavailable", web links are
  **always** underlined here; the hover refinement is folded into #318.
- **OSC-8 terminal hyperlinks** (the optional "gravy" `GetCellUrl` path) are **not** emitted
  here — they belong with click activation (**D**, #318) and are tracked as a follow-up.
- Markdown `[text](url)` spans and custom-id task URLs remain **B**'s deferred follow-ups
  (already tracked), unaffected by this slice.

## Test plan

### Unit — `tests/ClickUpTodo.Tests/DetailPaneViewTests.cs`

- `BuildCells` tags a **web** URL's cells `WebLink`, a **task** URL's cells `TaskLink`, and
  leaves the surrounding text `Normal` (offsets verified by slicing the cells back to text).
- A line with **no** URL stays entirely `Normal` (guards the existing "uncoloured content"
  invariant).
- A line **mixing** a task link and a web link tags each span with the right kind.
- The separator line is still `Separator` (unchanged) and link tagging never fires on it.
- Round-trips through the real `Cell.ToCellList`, so `ClassifyCell` reflects what actually
  lands in the cells.

### E2E — `tests/ClickUpTodo.Tui.E2E/link_check.py` (new)

The harness seeds a **task** link in the description and already seeds a **web** link
(`github.com/...`) in a comment. The check opens Task Detail, and:
- on **Description**, asserts the task-link URL cells are **underlined** and share the
  **same foreground** as normal body text (task styling = default fg + underline);
- on **Comments**, asserts the web-link URL cells are **underlined** and their foreground
  **differs** from normal body text and is **uniform** across the URL (web styling =
  recoloured + underline).

Relational assertions (recoloured vs not, underlined) keep the check robust to the exact
pyte colour encoding while the unit tests pin the concrete attributes.

### Regression

- `detail_check.py` A/B (text-only dump) stays **identical** — styling changes colour/
  underline, not glyphs.
- `color_check.py` A/B (diffed vs stock) stays identical on the main list (the detail panes
  aren't in that scenario).
