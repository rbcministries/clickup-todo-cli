# Checklists tab in Task Detail (C, #456)

Sub-issue **C** of the Task Checklists epic (#453). Depends on **B** (#455, `ChecklistArranger`
row projection — merged) and is gated by **#452** (bare `↑`/`↓` on Task Detail — merged via #467).
Read-only in this slice; the first write is **D** (#457).

## Goal

A new **Checklists** tab in `TaskDetailScreen`, a focusable `ListView` modelled on the Task Tree tab
(#291): it renders every checklist on the task — group headers with `resolved/total` progress, items
with `[x]`/`[ ]` glyphs, nested items indented, assignee shown where set — with the tab title carrying
aggregate progress (`Checklists (5/12)`). Present in **both** hosts (dashboard detail + single-task
launch mode), refresh-safe, with an empty-state row for a checklist-free task.

## Architecture fit

- The read model (`TaskDetail.Checklists`, #454) and the pure row projection
  (`ChecklistArranger.Project`, #455) already exist. C is thin glue over them plus a small pure
  display/refresh model — the same pure-glue split every detail feature uses (`TaskTreeArranger` +
  `TaskDetailScreen` glue; `DetailScrollModel`; `DetailTabNav`).
- No new focusable pane beyond the tab's own `ListView` — which is exactly the Task Tree precedent,
  itself a `ListView` tab inside the single sectioned detail screen. The dashboard's main-list
  single-`ListView` model (#3) is untouched.
- **No new chord.** The tab is reached by `Ctrl+←/→` (existing tab cycle) and driven by bare `↑`/`↓`
  (#452, existing `MoveActiveTab` `ListView` branch). So `Keybindings.cs` needs no new entry and the
  HelpLine sets already advertise `Ctrl+←/→` + `↑/↓`. Writes (Space, item/group CRUD) that *do* need
  chords are D–G.

## Pieces

### Pure (unit-tested) — `Services/ChecklistTabModel.cs`

A static class holding the Terminal.Gui-free half of the tab:

- `TabTitle(ChecklistProjection)` → `"Checklists (r/t)"` (aggregate resolved/total). Empty → `"Checklists"`.
- `EmptyStateText` → the single explanatory row a checklist-free task shows.
- `RenderRow(ChecklistRow)` → the display string: a header is `"{name}  (r/t)"`; an item is
  `{indent}{glyph}{name}{ — assignee}` where `indent` is `Depth*2` spaces and `glyph` is `[x] `/`[ ] `.
- `Signature(ChecklistProjection)` → a cheap content fingerprint (kinds, depths, texts, resolved,
  ids, counts, assignee) so a refresh only rebuilds the tab when its rendered content moved — the
  `OtherTabSignature` discipline applied to the checklist rows.
- `AnchorSelection(oldRows, oldIndex, newRows)` → the new selected index that keeps the cursor on the
  same *item id* (checklist id + item id + kind) when it still exists, else clamps the old index into
  range. So a content-changing refresh re-anchors by identity, not by position.

### Config — `Configuration/DetailTab.cs`

Add `DetailTab.Checklists` after `Other`, wiring **both** hand-written maps:
`Next()` (Stream → Description → Comments → Other → **Checklists** → Stream) and `ToTabIndex()`
(`Checklists → 4`). Checklists sits at index 4 in the base tab array (before the conditionally-appended
Task Tree tab), so the index is the same in both hosts. `SettingsScreen.DefaultTabText` gains the
`Checklists` case so F2 can select it as the persisted default.

### Glue — `Tui/Screens/TaskDetailScreen.cs`

- A `ListView _checklistList` (always built — not host-gated), added to the base `tabContents` /
  `scrollTargets` lists at index 4, before the Task Tree conditional (which stays appended last).
- `RenderChecklist(projection)` builds the display strings + a parallel `headerAttrs` array
  (`StatusBadgeListSource.NeutralHeaderAttr` on header rows, null on items) and assigns a
  `StatusBadgeListSource` (empty badges, `searchKeys` = row text for the #12 type-ahead); sets the tab
  title from `TabTitle`; caches the projected `_checklistRows` (for AnchorSelection + the D–G write
  slices) and `_checklistSignature`. Empty projection → the single `EmptyStateText` row.
- `UpdateData` recomputes the projection from `task.Checklists`; if `Signature` moved, captures the
  old rows + selected index, re-renders, and restores selection via `AnchorSelection`. Unchanged →
  no reassignment, so selection and scroll are preserved (the ListView keeps its own state).
- Existing generic machinery picks the tab up for free: `MoveActiveTab`'s `ListView` branch (bare
  `↑`/`↓`), `ScrollActiveTab`, `FocusCurrentPane`, `CycleTab`, and `DetailTabNav` are all
  array-length-driven. `ActiveTextPane()` returns null for the (non-`DetailPaneView`) checklist
  `ListView`, so the #319 bare-Tab/Enter link traversal correctly skips it; the tree's Enter/F6 are
  guarded on `_treeList` identity, so they don't fire here.
- Mouse: native single-click selection is sufficient for the read-only slice (no custom `MouseEvent`);
  D adds `Space`-toggle and can add a double-click then.

## Phases

1. **Config + pure model + tests** — `DetailTab.Checklists` (enum + `Next` + `ToTabIndex` +
   `SettingsScreen` text) with updated `DetailViewSettingsTests`; `ChecklistTabModel` +
   `ChecklistTabModelTests`. Push → opens the draft PR.
2. **TUI wire-in** — the `ListView` tab, `RenderChecklist`, refresh-safe `UpdateData` hook, both-host
   presence. Build + `dotnet test` green.
3. **E2E** — a seeded fake-backend checklist (nested items, mixed resolved state) behind a scenario
   env var in `tests/ClickUpTodo.Tui.E2E/Program.cs`; a new `checklist_check.py`; and the deliberate
   A/B-dump updates for `detail_check.py`, `tree_tab_check.py`, `tab_boundary_check.py`,
   `single_task_launch_check.py` (the extra tab shifts tab indices — assertions updated, not loosened).
   Then `gh pr ready`.

## Hard rules honoured

- No `Generated/` hand edit; no spec change / no regen (read comes free on the existing `GET /task`,
  already mapped by #454). Pure logic in `Services/`, glue untested-by-necessity per `CLAUDE.md`.
- Single sectioned `ListView` input model untouched — the checklist tab is a `ListView` inside the
  existing detail tab strip, exactly like the Task Tree tab; no second focusable pane on the main list.
- Refresh-safe: the 30s auto-refresh / `F5` don't reset selection or scroll when content is unchanged,
  and re-anchor by item id when it changed.

## Out of scope (later sub-issues)

Every mutation: toggle (**D** #457), item CRUD (**E** #458), group CRUD (**F** #459), assignee +
reorder/reparent (**G** #460). Header-row selection-skipping is deferred to D (where it first matters
for the toggle target); C lets selection land anywhere.
