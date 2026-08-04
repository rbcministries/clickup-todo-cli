# Checklists (B): pure row projection

Issue #455 — second slice of the Task Checklists epic #453, building directly on
the read model from **A** (#454, merged). No Terminal.Gui in this slice: turn the
domain `IReadOnlyList<TaskChecklist>` (groups → nested items, from
`TaskDetail.Checklists`) into one flat, display-ordered list of rows the tab (C,
#456) can render as thin glue. Mirrors the existing arranger split
(`TaskTreeArranger` / `SubtaskArranger`): the shape is pure and unit-tested; only
the Terminal.Gui glue in **C** stays untested-by-necessity.

## The shape

A new pure type `Services/ChecklistArranger.cs` with:

- **`ChecklistRowKind`** — `Header` (a checklist group: name + progress) or
  `Item` (one checklist item). The glue styles headers distinctly, the way the
  main list's group headers are (`GroupHeaderPalette`), and can make them
  non-selectable.
- **`ChecklistRow`** — a `readonly record struct` carrying:
  - `Kind`, `Depth` (indent level as a value, **not** baked-in spaces — the view
    picks the glyphs/indent), `Text` (the display name).
  - `ChecklistId` + `ItemId` (null on a header) so **D**–**G** never re-walk the
    tree from a selected index.
  - `Resolved` (item's `[x]`/`[ ]` state; the view renders the glyph).
  - `ResolvedCount` / `TotalCount` — the checklist's progress **on a header row**
    (0 on item rows), computed from the items actually projected so they always
    agree with what's shown (not the API's possibly-divergent
    `resolved`/`unresolved` counts).
  - `Assignee` — the projection-decided suffix text (the assignee's display
    name, or null when unassigned or only a bare unresolved id is known — id
    resolution is **G**'s job).
- **`ChecklistProjection`** — `Rows` + `ChecklistCount` + aggregate
  `ResolvedCount` / `TotalCount` (for a `Checklists (5/12)` tab title) + an
  `IsEmpty` flag the glue turns into an empty-state line.
- **`ChecklistArranger.Project(IReadOnlyList<TaskChecklist>)`** — the one entry
  point.

## Ordering, nesting, and the pathological cases

- **Ordering** — checklists by `OrderIndex`, items by `OrderIndex` within their
  parent. Deterministic and stable: a null/absent `OrderIndex` sorts *after* any
  present one, and every tie falls back to ordinal `Id`, so refreshes never
  reorder.
- **Nesting** — the read model carries nesting **both** ways ClickUp may express
  it (`ParentId` id-pointers *and* a populated `Children` array — #454). The
  arranger collects every distinct item once (walking the top level and, first,
  each item's `Children`), records the structural parent found via `Children`,
  and computes each item's *effective* parent: its `ParentId` when that points at
  an in-set item, else its structural parent, else it is a root. This
  reconstructs from either representation without double-counting an item that
  appears in both.
- **Orphan** (`ParentId` points at an id not in the set) → treated as a root and
  surfaced at top level, never dropped.
- **Cycle** (A↔B, or a self-parent) → the depth-first emit has a visited guard so
  it terminates; a straggler pass then surfaces any item a cycle left unvisited
  at top level, so every item appears exactly once. Mirrors `SubtaskArranger`'s
  cycle-safety net.

## Tests

`ChecklistArrangerTests` (pure `Fact`s) covering every acceptance bullet:
ordering across checklists and within a checklist; tie-break stability (equal
`OrderIndex` → id order; null `OrderIndex` sorts last); two and three levels of
nesting via `Children` and via `ParentId`; an orphaned child surfacing rather
than vanishing; a parent/child cycle terminating with every item present;
per-checklist and aggregate progress counts; an item with no assignee; zero
checklists (`IsEmpty`); a checklist with zero items (header only).

## Out of scope

The Checklists tab and any Terminal.Gui code (**C**, #456); all writes
(**D**–**G**).
