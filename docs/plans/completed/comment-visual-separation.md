# Plan — C1: clearer visual separation between comments (#105)

Part of the detail-view epic (#102). Goal: make each comment in the detail
view's **Comments** tab read as its own block, instead of being divided only by
a single, easy-to-miss blank line.

## Decision: formatter-only horizontal rule (the issue's low-risk first step)

The issue offers two routes:

1. **Formatter-only** — replace the bare blank line between comments with a
   horizontal-rule separator glyph. Pure string change in
   `TaskDetailFormatter.Comments`, covered by the existing formatter unit tests,
   no Terminal.Gui work.
2. **True non-shaded separator row** — a custom-drawn view mirroring
   `DetailAttributesView` so a divider renders on a different background
   attribute than the comment blocks. Heavier; reintroduces per-cell drawing.

We take **route 1**. The issue's own investigation found **no `ColorScheme`/
shading is applied** to the Comments pane (grep-confirmed) — the "perceived
gray" is just the default `TextView` background against the window, not an
explicit shade. So the real, confirmable cause is that a lone blank line is too
subtle. A rule glyph fixes exactly that with the least risk, and per-cell
background attributes (route 2) would be gold-plating for a plain-text pane.

## Design

- Add a shared `TaskDetailFormatter.CommentSeparator` constant: a fixed-width
  run of the box-drawing light-horizontal glyph `─`. Fixed width (not
  pane-width-derived) keeps the formatter pure and the tests deterministic; the
  Comments/Stream panes word-wrap, so it's sized long enough to read as a
  divider yet short enough not to fold on a normal-width terminal.
- In `Comments(...)`, between adjacent comments emit: blank line, the separator,
  blank line — so each comment is a clearly delimited block. The separator is
  **only between** comments: never before the first or after the last, and the
  `(no comments)` empty state is unchanged.
- Expose the constant `public` so the **Stream** renderer (#106) reuses the same
  separator style, per the epic's coordination note ("coordinate the formatter
  helper so both call it").

## Files

- `src/ClickUpTodo/Tui/TaskDetailFormatter.cs` — add `CommentSeparator`; rework
  the `Comments` loop to insert it between blocks.
- `tests/ClickUpTodo.Tests/TaskDetailFormatterTests.cs` — new cases.

No Terminal.Gui glue changes: the Comments tab is a plain word-wrapped
`TextView` already fed by `Comments(...)` (`TaskDetailScreen.cs:78`). Scroll
(↑/↓/PgUp/PgDn) and the `(no comments)` empty state are untouched.

## Tests (formatter unit tests, per the issue)

- Separator appears **between** two adjacent comments.
- Separator does **not** appear before the first / after the last comment
  (a single comment renders with no separator at all).
- Three comments ⇒ exactly two separators.
- Empty state `(no comments)` unchanged; single-comment output has no separator.
- Existing `Comments_*` tests stay green (author/text/resolved/empty-body).

## TUI verification

Build + reasoning (no Terminal.Gui code changed), plus a `tui-validate` PTY run
after `dotnet test` is green: open a task's detail, switch to the Comments tab,
confirm the rule renders between comments and scrolling still works, with no
output-volume or latency regression.

## Out of scope / deferred

- The heavier custom-drawn non-shaded separator **row** (route 2) — only worth
  it if the plain rule proves insufficient in review.
- The Stream tab itself (#106) — this only defines the separator it will reuse.
