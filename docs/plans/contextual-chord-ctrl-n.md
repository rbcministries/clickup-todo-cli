# Plan — Contextual chords (C): contextual `Ctrl+N` in Task Detail (#540)

Slice **C** of the contextual key/chord remapping epic (#537). Implements the
`Ctrl+N` half of the sub-context model recorded in slice **A**
(`docs/plans/contextual-chord-model.md`) and depends only on **A** (design,
merged) and **B** (#539, `F2` freed / Settings → `F10`, merged).

## Goal (from #540)

- `Ctrl+N` in Task Detail resolves by the **front-most tab**:
  - **Comments tab → new comment** (today's behaviour).
  - **Checklists tab → new checklist item** (replaces the `F7` add-item binding
    from #458).
  - Other tabs (Description / Other / Stream / Task Tree) → keep **new comment**
    (the sensible default per A).
- Retire the `F7` = `AddChecklistItem` binding; the add path is now `Ctrl+N` on
  the Checklists tab. `F8`/`F9` (rename/delete) are untouched — they move in
  slices D/F.
- The footer shows the correct `Ctrl+N` label per tab (`➕Comment` vs `➕ item`)
  and no longer advertises `F7`.
- `#355` cross-check green; `tui-validate` (`checklist_check.py`,
  `detail_comment_check.py`) updated and green; no latency / second-pane
  regression (#3).

## Design — realise the A model's `Ctrl+N` slice

Per `contextual-chord-model.md` §2, the base `(ScreenContext, KeyAction) →
token` map stays the single source of a token for an action; a new **tab
sub-context activation layer** declares which of the (now token-sharing) actions
is *live* per Task Detail tab.

### Keybindings.cs

- **New enum** `DetailSubContext { Default, Comments, Checklists, TaskTree }` —
  tab-scoped, orthogonal to the punted launch-mode dimension (#296).
- **Retarget** `AddChecklistItem`: token `F7 → Ctrl+N`. `AddComment` is already
  `Ctrl+N`, so within `Detail` two actions now share `Ctrl+N`; the sub-context
  disambiguates which is live. `AllBindingsOfAnAction_ShareOneKey` still holds —
  each *action* keeps one token; the sharing is *across* actions, which that
  test allows.
- **Activation table** `DetailActions: DetailSubContext → IReadOnlyList<KeyAction>`
  listing the *tab-scoped* actions per sub-context (the ones whose live-ness
  depends on the front tab). Context-wide Detail actions (`DispatchToClaude`,
  `ReplyToComment`, `EditDescription`, `OpenInBrowser`, `QuickUpdate`,
  `Refresh`, `Help`, `Back`) are **not** listed — they resolve unconditionally.
  For slice C the tab-scoped sets are:
  - `Checklists` → `AddChecklistItem`, `RenameChecklistItem`,
    `DeleteChecklistItem`, `ToggleChecklistItem`, `MoveChecklistItemUp/Down`,
    `OutdentChecklistItem`, `IndentChecklistItem`, `NewChecklist` (all already
    handler-guarded to the Checklists tab).
  - `Comments` / `Default` / `TaskTree` → `AddComment`.
- **`ResolveDetail(DetailSubContext sub, string token) → KeyAction?`** — the pure
  seam both dispatch and footer consult. Among the sub-context's active actions
  (falling back to `Default`'s) it returns the one whose base-`Map` token equals
  `token`. Anti-collision invariant: within one sub-context no token resolves to
  two active actions (so `Ctrl+N` → exactly `AddChecklistItem` on Checklists,
  exactly `AddComment` elsewhere).
- **`DetailBindings(DetailSubContext sub) → IEnumerable<(KeyAction, string)>`** —
  the active (action, token) pairs for a sub-context: the tab-scoped actions plus
  the context-wide ones. Drives the per-tab footer and its cross-check.

### TaskDetailScreen.cs (hand-rolled `OnKey`, not migrated to the dispatcher)

- Add `CurrentDetailSubContext()` — reference-compare `_tabs.Value` against
  `_checklistList` / `_treeList` / `_commentsPane`, else `Default` (the same seam
  the `F7/F8/F9` block and `HelpItems` already use).
- Route `Ctrl+N`: a new block (beside the other Checklists-tab guards) fires
  `AddChecklistItem()` when `ResolveDetail(CurrentDetailSubContext(), "Ctrl+N") ==
  AddChecklistItem` — i.e. only on the Checklists tab. On every other tab it
  returns without handling, so the existing generic `Ctrl+N` block still opens
  the comment composer.
- Retire `F7`: the `F7/F8/F9` block becomes `F8/F9` only; drop the `F7` case.
- Pass the sub-context to `HelpItemSets.DetailFooter`.

### HelpLine.cs

- `DetailFooter(...)` gains a `DetailSubContext sub` parameter. The non-overlay
  Detail set relabels the single `Ctrl+N` item — `➕ item` on `Checklists`,
  `➕Comment` otherwise — and drops the `F7 ➕ item` item (retired). The overlay
  precedence (mention picker / composer / editor / reply picker / checklist item
  editor) is unchanged.

### Tests (KeybindingsTests.cs)

- Keep the existing base-`Map` cross-checks green (`FooterFor(Detail)` uses the
  `Default` footer, which still carries a `Ctrl+N` item, so both `AddComment` and
  `AddChecklistItem` resolve).
- Add per-sub-context tests: `ResolveDetail` maps `Ctrl+N` to `AddChecklistItem`
  on `Checklists` and `AddComment` elsewhere; the anti-collision invariant (no
  token → two active actions within a sub-context); and a per-`DetailSubContext`
  footer cross-check (each sub-context's footer shows every active binding under
  its token, with the right `Ctrl+N` label).

## Out of scope (later slices)

- `F2` = rename (D/E), `Delete` + confirmation modal (F), `Ctrl+N` sibling-vs-
  child clarification (G), main-list `F2` rename (H). This slice only makes
  `Ctrl+N` contextual and retires `F7`.

## Verification

- `dotnet build -c Release` (0 warn/0 err), `dotnet test -c Release`
  (integration skips without creds), `dotnet format --verify-no-changes`.
- `tui-validate`: `checklist_check.py` (add now via `Ctrl+N`, not `F7`) and
  `detail_comment_check.py` (composer still opens on `Ctrl+N` on the default
  tab) green; no second focusable pane / latency regression (#3).
