# Subtasks: expand-all / collapse-all — issue #83

Adds a bulk **expand-all / collapse-all** affordance to the F4 subtasks view,
complementing the per-parent `→`/`←` fold shipped in #76. Follow-up deferred
from #76 (PR #82).

## Settling the issue's open design questions

### Q1 — Binding: **`Ctrl+→` = expand-all, `Ctrl+←` = collapse-all**

The issue offered three options (a chord like `Shift+←`/`Shift+→`, a dedicated
function key, or a three-way `F4` cycle). Decision: **`Ctrl+→`/`Ctrl+←`**.

Rationale:

- **Mnemonic parity with the per-parent fold.** `→`/`←` expand/collapse the
  *selected* parent; `Ctrl+→`/`Ctrl+←` do it for *all* parents. Same direction,
  "with Ctrl = apply to everything" — trivially learnable.
- **Matches the codebase's command model.** Every command shortcut here is a
  `Ctrl` chord or function key (`key.IsCtrl` dispatch in `OnListKey`); bare
  letters are reserved for the ListView type-ahead (#12). A `Ctrl` chord slots
  straight into the existing `key.IsCtrl` branch.
- **Driver / terminal-emulator reliability (the #3 lineage).** `Shift+Arrow` is
  frequently *captured by the terminal emulator itself* for text selection and
  never forwarded to the TUI; `Ctrl+Arrow` (`CSI 1;5C`/`1;5D`) is reported
  reliably across the `ansi`/`dotnet`/`windows` drivers. Given how much this
  project cares about input fidelity, `Ctrl` is the safer chord.
- Rejected the three-way `F4` cycle: it overloads `F4`'s established on/off gate
  semantics (muscle memory) and is less discoverable than a distinct chord.

Alternative for the maintainer: if `Shift+←`/`Shift+→` is preferred, it's a
one-line binding swap — noted in the PR.

### Q2 — Scope of "all": **every foldable parent in the current view, walked from the snapshot**

The issue notes a collapsed parent's children aren't in the rendered row list,
so "expand all" must walk the snapshot, not the rendered rows. Implemented via a
new pure helper that derives foldable-parent ids structurally from the task set:

- **Collapse-all:** clear `_expanded` (already the default state) → all parents
  collapse.
- **Expand-all:** add **every foldable parent id** — a task that is *present* in
  the view and has at least one *present* child (at any depth) — to `_expanded`,
  computed over the whole candidate universe (`_all` ∪ pulled-in foreign
  subtasks, #70). Context parents (#46) are never foldable and are excluded.

Over-approximation is safe: an id in `_expanded` that isn't actually a foldable
row in a given section is ignored by `SubtaskArranger` (its `Fold` stays
`None`). The helper cannot *miss* a foldable parent, because any foldable
parent — present, with a present child referencing it — is captured by the
structural scan.

### Q3 — Reflect the shortcut in the footer help line and `HelpScreen`. Done.

## Changes

### Pure / unit-tested (`Services/SubtaskArranger.cs`)

- New `SubtaskArranger.FoldableParentIds(IReadOnlyList<TaskItem> tasks)` →
  `IReadOnlySet<string>`: the ids of every task present in `tasks` that has at
  least one present child. Independent of any current fold state, so it drives
  expand-all across every depth. Ordinal comparer, matching `_expanded`.
- Tested in `SubtaskArrangerTests`: flat list → empty; a parent with a child →
  that parent; deep chain → every intermediate parent (all depths); a parent
  whose only child is absent → excluded; an orphan pointing at a missing parent
  → excluded (parent not present); cycle-safe; and cross-check that the returned
  set matches exactly the parents `Arrange(..., expanded: all-of-them)` marks
  `Expanded`.

### TUI wiring (`Tui/TodoApp.cs`, verified by build + reasoning + tui-validate)

- `OnListKey`: in the `key.IsCtrl` branch, add `CursorRight`/`CursorLeft` cases,
  gated on `_config.View.ShowSubtasks && _activeScreen is null` (mirrors the
  bare-arrow gate). When the subtasks view is off, they stay unhandled and fall
  through to native behaviour.
- `ExpandAll()`: `_expanded.UnionWith(SubtaskArranger.FoldableParentIds(candidateUniverse))`,
  Flash a short confirmation, `Render(keepTaskId: CurrentTask()?.Id)` (the
  current row stays visible — expanding only reveals more).
- `CollapseAll()`: `_expanded.Clear()`, then keep the cursor on the current
  task's **top-level ancestor** (its own row is hidden if it was a nested child)
  via a small parent-chain walk over the candidate universe, Flash, Render.
- The candidate universe helper unions `_all` with `_foreignSubtasks.Values`.

### Footer + Help

- Footer label: append the `Ctrl` bulk chord next to the per-parent `→/←`.
- `HelpScreen`: add a line documenting `Ctrl+→ / Ctrl+←  Expand / collapse all`.

## Out of scope

- No change to F4's on/off gate, the per-parent `→`/`←` behaviour, the fetch
  strategy, or persistence (`_expanded` stays session-only, per #76).
- No new focusable pane; the single sectioned `ListView` model is preserved.
