# Per-parent subtask expand/collapse with ←/→ (▶/▼) — issue #76

Generalises the all-or-nothing F4 subtasks view (#46/#57/#75) into **per-parent**
expand/collapse driven by the arrow keys, with a leading ▶ (collapsed) / ▼
(expanded) marker.

## Decisions (settling the issue's open design questions)

The one **DECIDED** item in the issue is the type-ahead approach: **option 2 —
override `StatusBadgeListSource.ToList()` to return title-only search keys**, so
the rendered line can carry a `▶`/`▼` marker without regressing #12.

The remaining "Proposing …" questions, settled here:

1. **Default fold state: collapsed** (issue Q1) — a clean top-level list; expand
   on demand with →.
2. **Persistence: session-only** (issue Q2) — the expanded-parent id set is
   in-memory state on `TodoApp`, not persisted to `AppConfig`.
3. **F4's role: stays the master on/off gate (option `c`)**, *not* repurposed as
   expand-all/collapse-all (the issue's proposed `a`). Rationale:
   - Nesting **and** the context-parent network fetch
     (`ResolveContextParentsAsync`, run only when `ShowSubtasks` is on) are gated
     behind `ShowSubtasks`. Making nesting always-on (option `a`) would fetch
     context parents on *every* refresh even in the default view — a real
     round-trip regression.
   - Keeps the default (F4-off) experience byte-for-byte: flat top-level list, no
     markers, no extra fetch.
   - `←/→` per-parent folding operates **within** the on state, fully satisfying
     the acceptance criteria.
   - Expand-all / collapse-all (proposal `a`) is deferred to a follow-up issue.
4. **Context parents are exempt from folding** (always shown, no marker). A
   context parent (#46) exists *only* to display an assigned subtask whose real
   parent isn't mine; collapsing it would hide my own assigned work. So it never
   gets a ▶/▼ marker and its children always render.
5. **← on a child** jumps to and collapses its (foldable) parent (issue Q4).
   **→ on an expanded parent** moves into its first child (issue Q5). **→/←** on
   a leaf / top-level non-parent is a no-op.
6. **←/→ replace horizontal scroll** only while the subtasks view is on; off, the
   ListView keeps its native horizontal scroll.

## Data-model changes (pure, unit-tested)

- **`FoldState` enum** (`Services`, next to `ArrangedRow`): `None` (leaf / not a
  parent / context parent), `Collapsed`, `Expanded`.
- **`ArrangedRow`** gains `FoldState Fold { get; init; }` (default `None`) — an
  `init` property so existing positional constructions keep compiling.
- **`LayoutRow`** gains `FoldState Fold { get; init; }` (default `None`),
  threaded from the arranged row.
- **`SubtaskArranger.Arrange(orderedTasks, contextParents, expanded = null)`**:
  a new optional `IReadOnlySet<string>? expanded` param.
  - `expanded == null` ⇒ **all parents expanded** (backward-compatible: existing
    2-arg callers/tests are unchanged).
  - non-null ⇒ a parent is expanded **iff** its id is in the set.
  - A **collapsed** parent's whole subtree is *suppressed* (marked emitted, no
    rows) so it never leaks out flat via the outer loop / safety net.
  - Each emitted parent row carries `Fold` (`Expanded`/`Collapsed`), leaves/
    context parents carry `None`.
- **`SectionLayout.BuildTodoSection(…, expanded = null)`** and
  **`FocusSectionLayout.Build(…, expanded = null)`** forward the set to the
  arranger (default `null` keeps existing tests green).
- **`TaskRowFormatter.Format(task, depth, isContextParent, groupedBy, marker = "")`**:
  a new optional `marker` string prepended right after the indent, so the
  incremental badge-offset math automatically accounts for it (existing callers
  pass `""` ⇒ byte-for-byte unchanged).

## TUI wiring (`TodoApp`, verified by build + reasoning)

- **`_expanded`**: `HashSet<string>` session state (parent ids, empty = all
  collapsed).
- **Parallel row arrays** gain `_folds` (`List<FoldState>`) and `_markers`
  (`List<string>`) so an in-place status update (`UpdateTaskRow`) reproduces the
  correct marker, and `←/→` can consult the selected row's fold state.
- **Marker mapping** (only when `nest`): `Expanded → "▼ "`, `Collapsed → "▶ "`,
  `None → "  "` (a 2-col gutter so titles align under sibling markers). When
  `nest` is off, marker is `""` (unchanged).
- **Search keys**: a new `_searchKeys` list parallel to `_display` — the **title
  only** for task rows, the display text for header/spacer rows — passed to
  `StatusBadgeListSource`. `ToList()` returns these so #12 type-ahead matches
  titles regardless of the marker/badges.
- **`OnListKey`**: handle `CursorRight`/`CursorLeft` when `nest` is on:
  - **→** on a collapsed parent → expand; on an expanded parent → select first
    child; else no-op.
  - **←** on an expanded parent → collapse; on a child/collapsed row → select &
    collapse its foldable parent (jump to a context parent without collapsing);
    else no-op.
- **Footer help line + `HelpScreen`** mention `←/→ expand/collapse subtasks`.

## `StatusBadgeListSource`

- New optional ctor param `IReadOnlyList<string>? searchKeys`. `ToList()` returns
  the keys (title-only) when supplied, else delegates to the stock wrapper
  (unchanged). All other behaviour is untouched (single sectioned ListView, no
  second focusable pane).

## Tests

- `SubtaskArrangerTests`: expanded-set honoured — a collapsed parent hides its
  subtree (and doesn't leak flat); an expanded parent nests; `Fold` values are
  correct; context parents stay expanded regardless of the set; `expanded == null`
  keeps the legacy all-expanded behaviour.
- `SectionLayoutTests` / `FocusSectionLayoutTests`: per-parent folding composes
  with grouping and with pinned nesting (#75).
- `StatusBadgeListSourceTests`: `ToList()` returns the title-only keys (no ▶/▼,
  no badges) — the #12-preserving seam.
- `TaskRowFormatterTests`: `marker` prefix is prepended and badge spans stay
  exact with a marker present.

## Out of scope / deferred (follow-up issue)

- **Expand-all / collapse-all** (issue Q3 proposal `a`).
- **Badges-first row reorder** — already noted in the issue as a separate
  follow-up the maintainer will file; the `ToList()` decoupling here is the
  enabler.
- Fetching a parent's not-in-snapshot children (#70).
