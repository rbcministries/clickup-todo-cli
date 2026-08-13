# Plan — Contextual chords (A): the contextual-chord model + F2/Ctrl+E design (#538)

Slice **A** of the contextual key/chord remapping epic (#537) — the foundational **decision**
the per-tab remaps (C–G) depend on. Its whole deliverable is this note plus the decisions
recorded on #538; **no production code changes here** (C–G implement against it).

The maintainer recorded the three decisions on #538
([comment, 2026-08-11](https://github.com/rbcministries/clickup-todo-cli/issues/538#issuecomment-5255678537)):

1. **Model:** add a **tab/sub-context dimension** to the keybinding table.
2. **F2 ⇄ Ctrl+E:** in Task Detail, **F2 is an alias for `Ctrl+E`** in every context where F2 does
   *not* "rename the highlighted item" (e.g. a checklist item, a task in the Task Tree).
3. **Modals:** native Terminal.Gui modals **are accepted** as the path forward.

Both prerequisites have landed: **B (#539)** freed `F2` (Settings → `F10`, PR #553, merged) and the
**#404 native-modal spike** (PR #555) plus its **#554** focusable-form follow-up (PR #562) both
returned **GO** recommendations. This note turns those decisions into an implementable model.

---

## 1. The crux (why a decision was needed)

The keybinding source of truth (#355) is a **flat** `(ScreenContext, KeyAction) → key` map in
`src/ClickUpTodo/Tui/Screens/Keybindings.cs:95-189`, with public lookups `Token` / `TryToken` /
`ActionsFor` (`:196-208`). Two consumers read it, so a chord and its label can never drift:

- **Dispatch** — `KeybindingDispatcher` (`src/ClickUpTodo/Tui/KeybindingDispatcher.cs`): a screen calls
  `.On(action, handler)` (`:33-47`), which resolves the token from the table, parses it once with
  `Key.TryParse`, and registers `KeyCode → handler`; `Dispatch(key)` (`:54-63`) fires the match. `.On`
  **fails fast if two actions collide on one `KeyCode` in a context** (`:42-44`).
- **Footer** — `HelpItemSets` (`src/ClickUpTodo/Tui/Screens/HelpLine.cs:162-444`), one item list per
  context, cross-checked against the table by `KeybindingsTests.Footer_ShowsTheTableKey_ForEveryBinding`
  (`tests/ClickUpTodo.Tests/KeybindingsTests.cs:56-65`): *for every `(context, action, token)` in
  `Keybindings.All`, the context's footer must contain an action item whose `ActionKey == token`.*

`ScreenContext` (`Keybindings.cs:9-25`) is **deliberately flat** — "no launch-mode dimension yet;
that is #296, deliberately out of scope." It has **no per-tab dimension**. But #537 needs one key to
mean different things per Task Detail tab:

| Key      | Comments tab        | Checklists tab            | Task Tree tab        | Description / Other / Stream |
| -------- | ------------------- | ------------------------- | -------------------- | ---------------------------- |
| `Ctrl+N` | new **comment**     | new **checklist item**    | (default: comment)   | (default: comment)           |
| `F2`     | *alias `Ctrl+E`* (edit) | **edit item** — name **+ assignee** (#572) | rename **task** title | *alias `Ctrl+E`* (edit) |
| `Delete` | (later: delete comment) | delete **checklist item** | —                | —                            |

`F2` is written "edit" not "rename" deliberately — see §3: it opens the *edit surface* for the
highlighted thing, of which renaming is one facet.

Today that scoping is done *informally*, entirely inside Task Detail's monolithic hand-rolled
`OnKey` (`src/ClickUpTodo/Tui/Screens/TaskDetailScreen.cs:1085-1426`) — **Detail was never migrated to
`KeybindingDispatcher`** (the `central-keybinding-dispatcher.md` plan migrated only the main list and
deferred the rest). The existing `F7/F8/F9` block is the precedent to generalize — one key already
resolves by **front tab *and* selected-row kind**:

```csharp
// TaskDetailScreen.cs:1178-1202
if (ReferenceEquals(_tabs.Value, _checklistList)
    && key.KeyCode is KeyCode.F7 or KeyCode.F8 or KeyCode.F9)
{
    key.Handled = true;
    var onHeader = SelectedChecklistRow() is { IsHeader: true };
    switch (key.KeyCode) {
        case KeyCode.F7: AddChecklistItem(); break;
        case KeyCode.F8: if (onHeader) RenameSelectedChecklistGroup(); else RenameSelectedChecklistItem(); break;
        case KeyCode.F9: if (onHeader) DeleteSelectedChecklistGroup(); else DeleteSelectedChecklistItem(); break;
    }
    return;
}
```

The front-most tab is tracked by `Terminal.Gui`'s `_tabs.Value` (reference-compared against
`_checklistList` / `_treeList`; there is no separate index field). The footer already varies by
overlay/tab-*presence* through the pure `HelpItemSets.DetailFooter(...)` precedence chain
(`HelpLine.cs:321-330`) — but **not** by which read-tab is front-most; `F7/F8/F9` + `Ctrl+N` hints show
unconditionally today.

So the decision was: keep resolving in imperative handler code (the "runtime resolution layer" option),
**or** lift the sub-context into the table itself. The maintainer chose the latter.

## 2. Decision 1 — the model: a tab/sub-context dimension in the table

Introduce a **sub-context** dimension, scoped to Task Detail for now, expressed as **table data** (not
imperative branching) so it stays under the #355 anti-drift cross-check and is reflected in the footer.

### 2.1 The sub-context enum

```csharp
// Keybindings.cs — new, alongside ScreenContext
public enum DetailSubContext { Default, Comments, Checklists, TaskTree }
```

`Default` covers Stream / Description / Other and is the fallback for any tab without an override. It
is **tab-scoped and orthogonal to launch-mode (#296)** — this does not reintroduce the punted
launch-mode dimension. Keep it Detail-specific: only Detail needs per-tab chords today, and a general
`(Context, SubContext, Action)` triple would churn every existing row for no benefit. If a second
screen ever needs sub-contexts, promote the pattern then.

### 2.2 Keep tokens single-sourced; add an *activation* layer

Do **not** widen the base `Map` key to a triple. Instead:

- **The base `Map` stays `(ScreenContext, KeyAction) → token`** — still the single source of the *token
  for an action*. This preserves `AllBindingsOfAnAction_ShareOneKey` (`KeybindingsTests.cs:37-48`): one
  action → one key, everywhere. The per-tab remaps only change *which* token an action carries:

  | Action                | Token today | Token after epic | Slice |
  | --------------------- | ----------- | ---------------- | ----- |
  | `AddComment`          | `Ctrl+N`    | `Ctrl+N`         | —     |
  | `AddChecklistItem`    | `F7`        | **`Ctrl+N`**     | C     |
  | `RenameChecklistItem` | `F8`        | **`F2`**         | D     |
  | `DeleteChecklistItem` | `F9`        | **`Delete`**     | F     |
  | `RenameTask` *(new)*  | —           | **`F2`**         | E     |
  | `EditDescription`     | `Ctrl+E`    | `Ctrl+E`         | E     |

  After C/D/E, within `Detail` **two actions share `Ctrl+N`** (`AddComment`, `AddChecklistItem`) and
  **two share `F2`** (`RenameChecklistItem`, `RenameTask`). The sub-context disambiguates *which is
  live*.

- **Add a per-sub-context activation table** — the new dimension — declaring which of the ambiguous
  actions is active in each Detail sub-context:

  ```csharp
  // Keybindings.cs — the tab/sub-context dimension (data, not code)
  private static readonly IReadOnlyDictionary<DetailSubContext, IReadOnlyList<KeyAction>> DetailActions =
      new Dictionary<DetailSubContext, IReadOnlyList<KeyAction>>
      {
          [DetailSubContext.Comments]   = [KeyAction.AddComment, KeyAction.ReplyToComment, KeyAction.RenameTask /*F2 alias→Ctrl+E*/],
          [DetailSubContext.Checklists] = [KeyAction.AddChecklistItem, KeyAction.RenameChecklistItem, KeyAction.DeleteChecklistItem, KeyAction.ToggleChecklistItem],
          [DetailSubContext.TaskTree]   = [KeyAction.RenameTask],
          [DetailSubContext.Default]    = [KeyAction.AddComment, KeyAction.RenameTask /*F2 alias→Ctrl+E*/],
      };
  ```

  (Context-wide Detail actions — `DispatchToClaude`, `EditDescription`, `OpenInBrowser`, `QuickUpdate`,
  `Refresh`, `Help`, `Back`, `NewChecklist` — stay live in every sub-context and are not listed per
  tab; they resolve exactly as today.)

- **Resolution** is a new pure lookup — the seam both dispatch and footer consult:

  ```csharp
  // Keybindings.cs
  //   The KeyAction the front-most Detail tab binds `token` to, or null if the tab doesn't bind it.
  //   Anti-collision invariant: within one sub-context, no token maps to two active actions.
  public static KeyAction? ResolveDetail(DetailSubContext sub, string token);
  //   The active (action, token) pairs for a Detail sub-context — what the footer renders.
  public static IEnumerable<(KeyAction Action, string Token)> DetailBindings(DetailSubContext sub);
  ```

  `ResolveDetail` walks the sub-context's active actions, resolves each to its base-`Map` token, and
  returns the action whose token matches — with `Default` as the fallback when the front tab has no
  override. This is table-driven: the token is never re-typed at a handler call site.

### 2.3 Dispatch: Detail keeps its hand-rolled handler for now

Detail is **not** migrated to `KeybindingDispatcher`, and migrating it is **out of scope for this
epic** (it's a large, separate refactor). C–G therefore keep the literal `OnKey`
(`TaskDetailScreen.cs:1085-1426`), but replace the `Ctrl+N` / `F7` / `F8` / `F9` literal branches with a
single table-driven step that mirrors the F7/F8/F9 precedent, now sourced from `Keybindings`:

```csharp
// front-most tab → DetailSubContext (reference-compare on _tabs.Value, as the F7/F8/F9 block does)
var sub = ReferenceEquals(_tabs.Value, _checklistList) ? DetailSubContext.Checklists
        : ReferenceEquals(_tabs.Value, _treeList)      ? DetailSubContext.TaskTree
        : ReferenceEquals(_tabs.Value, _commentsTab)   ? DetailSubContext.Comments
        : DetailSubContext.Default;

if (Keybindings.ResolveDetail(sub, Tokenize(key)) is { } action)
{
    key.Handled = true;
    switch (action)
    {
        case KeyAction.AddComment:          ShowCommentComposer(); break;
        case KeyAction.AddChecklistItem:    AddChecklistItem(); break;
        case KeyAction.RenameChecklistItem: RenameSelectedChecklistItem(); break;   // header → group (row-kind, unchanged)
        case KeyAction.DeleteChecklistItem: ConfirmDeleteSelectedChecklistItem(); break;  // native modal, slice F
        case KeyAction.RenameTask:          RenameCurrentTaskOrAliasCtrlE(); break;  // slice E
        // ... context-wide actions unchanged
    }
    return;
}
```

The **row-kind** sub-scoping (`onHeader` → group vs item) stays in the handler — it is genuinely
imperative selection state, below the granularity a static table should carry. The table owns *tab →
action*; the handler owns *row → target within that action*. Optionally, a later cleanup can add a
sub-context-aware `KeybindingDispatcher.On(sub, action, handler)` / `Dispatch(key, sub)` overload and
migrate Detail wholesale, but that is **not required** by this epic and should not block C–G.

### 2.4 Footer: `DetailFooter` gains the front tab

Extend `HelpItemSets.DetailFooter(...)` (`HelpLine.cs:321-330`) with the front-most `DetailSubContext`
so the base (non-overlay) Detail set is chosen per tab, driven by `DetailBindings(sub)`:

- Comments → `Ctrl+N ➕Comment`, `Ctrl+T ↩Reply`, `F2 ✏Edit` (aliases `Ctrl+E`).
- Checklists → `Ctrl+N ➕Item`, `F2 ✏Edit` (name + assignee, #572), `Delete 🗑Delete`, `Space ☐Toggle`.
- Task Tree → `F2 ✏Rename` (title only), plus the existing `F6`/`Ctrl+Enter` tree hints.
- Default (Description/Other/Stream) → `Ctrl+N ➕Comment`, `F2 ✏Edit` (aliases `Ctrl+E`).

The `F2` label is **Edit** wherever the surface edits more than a title (checklist item, and the
`Ctrl+E`-alias tabs) and **Rename** only where a title is the sole editable facet (Task Tree) — §3.

The overlay sets (composer / editor / picker) keep their existing top precedence — an open overlay
still owns the footer, unchanged.

## 3. Decision 2 — F2 ⇄ Ctrl+E in Task Detail: "Rename / Edit"

The #538 comment phrases it as "`F2` renames the highlighted item," but the operative meaning is
**broader than a title-only rename, and it must be** — because the surface `F2`/`Ctrl+E` opens is an
*edit* surface, not a rename box. For a checklist item, **#572** puts a per-item **assignee** control
*inside the same modal as the name field*, so the one `F2` gesture edits **name + assignee** (and is the
natural home for any future per-item field). So the honest framing is:

> **`F2` = "Rename / Edit" the highlighted thing** — it opens the edit surface for whatever is in front
> of you. *Rename* is the label where a name/title is the only editable facet; *Edit* where the surface
> carries more.

Realized three ways, by what the highlighted thing's edit surface holds:

- **Checklists tab → edit the item** (`RenameChecklistItem`; header row → the group). The modal edits
  the item **name + assignee** (#572), so its footer/label reads **Edit**, not Rename. (The action's
  code name is still `RenameChecklistItem` from #458; slice D/#572 may rename it to `EditChecklistItem`
  to match — a cosmetic code change, not a model change. The *token* and *sub-context slot* are what
  this note fixes.)
- **Task Tree tab → rename the task** (`RenameTask` targeting the highlighted node). Here the only
  editable facet is the **title**, so *Rename* is accurate.
- **Every other tab** (Comments / Description / Other / Stream) — no per-row item, so **`F2` is a pure
  alias of `Ctrl+E`**, the existing **edit** chord. `Ctrl+E` remains `EditDescription`; slice **E**
  additionally gives `Ctrl+E` the ability to **rename the current task's title** (the API already
  accepts `UpdateTaskRequest.name`, so slice E adds only a `SetTaskNameAsync` facade + `TaskService`
  passthrough — **no spec change / Kiota regen**). `F2`'s alias inherits whatever `Ctrl+E` does — which
  is why these tabs also read **Edit**, not Rename.

This is exactly *why* the convention-named `F2` (= Rename) can alias `Ctrl+E` (= Edit description)
without contradiction: **both are "edit the contextual thing," and rename is one facet of edit.**
`Ctrl+E` is **not** replaced by `F2`; `F2` is the convention front door that maps onto `Ctrl+E`'s
capability where no sub-item is selected, and onto the item/task edit where one is. This keeps the #290
convention (`F2` = Rename) as the *mnemonic* while being honest that the surface behind it edits more
than a name wherever the context offers more (checklist item = name + assignee, #572).

## 4. Decision 3 — modals: native Terminal.Gui

Native modals are **accepted** for the two modal-bearing slices:

- **F — contextual `Delete` + confirmation.** A native confirm dialog, superseding the inline
  `Enter`/`Esc` confirm for #458 items (`TaskDetailScreen.cs:1101-1117`) and #459 groups (`:1119-1139`).
- **G — `Ctrl+N` sibling-vs-child clarification.** A native choice dialog ("Add {Task|Comment|item}"
  vs "{Subtask|Reply|sub-item}") that, on the child choice, builds the parent association (subtask
  parent; reply target #330; checklist sub-item parent #460).

Grounding: the #404 spike (`docs/plans/completed/native-modals-spike.md`) and the #554 focusable-form
A/B both found **no #3/#38 latency regression and no #346 dispose crash** on Terminal.Gui 2.4.10 with
the current ANSI renderer — intra-modal key→paint ~43 ms (A) vs ~44 ms (B), post-modal list-nav
~65 ms (A) vs ~64 ms (B), with `Result`-marshalling and modal-stacking both working.

**Enabling work F/G inherit:** native modals exist today only as the **flag-gated spike**
`src/ClickUpTodo/Tui/NativeModalSpike.cs` (two bespoke static methods behind `CLICKUP_TODO_NATIVE_MODAL`,
off by default) — *not* reusable infrastructure. Before F/G can ship a real confirm/choice dialog, the
spike's shape (nested `Application.Run(dialog)` disposed through
`TuiTeardown.DisposeSwallowingTeardownBug`, plus the `_open`/`TryBeginOpen` slot guard) must be
promoted into a small **reusable modal helper** (e.g. a `ConfirmDialog` / `ChoiceDialog`). Per the
spike's own recommendation, keep the native path **behind the flag until the `windows` and `dotnet`
drivers are confirmed** (the `tui-validate` harness is ANSI-only), then flip the default. F owns the
promotion (it lands the first real modal); G reuses it.

## 5. What each downstream slice implements against this

- **B (#539)** — done: Settings → `F10`, `F2` freed (merged).
- **C (#540)** — retarget `AddChecklistItem` token `F7 → Ctrl+N`; add `DetailSubContext` + the
  activation table + `ResolveDetail`; route `Ctrl+N` through it (Comments → comment, Checklists → item).
  Retire the `F7` handler branch.
- **D (#541)** — retarget `RenameChecklistItem` token `F8 → F2`; `Delete` handled in F. After C/D/F,
  **no `F7`/`F8`/`F9` binding remains** anywhere. `F2` here opens the item **edit** modal (name today;
  **#572** adds the assignee field into that same modal), so label it **Edit** and consider renaming the
  action `RenameChecklistItem → EditChecklistItem` (§3) — cosmetic, coordinate with #572.
- **E (#542)** — add `KeyAction.RenameTask` (token `F2`) + `SetTaskNameAsync` facade; wire F2 on the
  non-item tabs as the `Ctrl+E` alias and give `Ctrl+E` task-title rename.
- **F (#543)** — retarget `DeleteChecklistItem` token `F9 → Delete`; promote the native-modal spike to
  a reusable confirm dialog; contextual `Delete`.
- **G (#544)** — the sibling-vs-child native choice dialog on `Ctrl+N`; parent association wiring.
- **H (#545, deferred)** — `F2` = rename in the **main task list** and Task Tree tab, reusing E's
  facade. Note: this extends the sub-context idea to `ScreenContext.MainList`; when H lands, generalize
  the activation layer beyond Detail (or add a `MainList` `F2 → RenameTask` binding directly, since the
  list has no ambiguous tabs).

## 6. Updating the #355 cross-check (the guard that must change)

Binding `F2` and doubling `Ctrl+N`/`F2` onto two actions each will trip two existing guards — both must
be **updated, not weakened**, to assert the stronger sub-context invariant:

- **`Settings_IsF10_OnMainList_AndNoBindingUsesF2` (`KeybindingsTests.cs:119-124`)** — its
  `Assert.DoesNotContain(..., e => e.Value == "F2")` becomes false the moment D/E bind F2. Replace the
  "F2 is unbound" clause with the real invariant it was a placeholder for: **within any single Detail
  sub-context, no token resolves to two active actions** (the sub-context analogue of
  `KeybindingDispatcher.On`'s collision guard, `KeybindingDispatcher.cs:42-44`). Keep the
  `Settings == F10` assertion.
- **`Footer_ShowsTheTableKey_ForEveryBinding` (`:56-65`)** and **`FooterFor` (`:14-31`)** — today
  `FooterFor(Detail) => HelpItemSets.Detail`, a single set. Generalize the Detail row to quantify over
  `DetailSubContext`: for each sub-context, the sub-context's footer set must show every *active*
  action's token. This keeps "footer ⊇ table" at the finer sub-context granularity, so the Comments
  footer proves `Ctrl+N ➕Comment` and the Checklists footer proves `Ctrl+N ➕Item` and `F2 ✏Edit`
  independently.
- `AllBindingsOfAnAction_ShareOneKey` (`:37-48`) still holds unchanged — each *action* keeps one token
  (`AddChecklistItem` is only ever `Ctrl+N`; the sharing is across actions, which this test allows).
- `EveryToken_IsParseable` (`:79-86`) — `F2` / `Delete` both round-trip through `Key.TryParse` (add
  coverage).

## 7. Reconciliation & invariants

- **#402 (navigation taxonomy).** F/G's confirm/choice dialogs are exactly #402's **transient-modal**
  category. This note commits only the *new* F/G dialogs to native surfaces; the **wholesale migration**
  of the existing transient modals is no longer "#402's open call" (that issue is closed) — it is now
  decided and owned by the accepted navigation ADR, `docs/navigation-model.md` ("Transient-modal
  migration to native Terminal.Gui modals", via #614): pilot Quick Open, flag-gated behind
  `CLICKUP_TODO_NATIVE_MODAL` until the `windows`/`dotnet` drivers are confirmed. F/G stay consistent
  with that policy — the `ConfirmDialog`/`ChoiceDialog` they promote are the reusable shape those
  migrations reuse.
- **#290 (shortcut standardization).** F2 = Rename, F10 = Settings, `Delete` = Delete, one `Ctrl+N`
  "new" chord — all move toward #290's conventions.
- **#506 (config chord-override layer).** The sub-context activation layer is precisely where a future
  config override would patch tokens; #506 must override *tokens in the base `Map`* while the
  sub-context activation (which action is live per tab) is preserved. Noted so #506 doesn't flatten the
  dimension.
- **#296 (launch-mode dimension, punted).** The new dimension is **tab**-scoped, orthogonal to
  launch-mode; this does not resurrect #296.
- **#12 (type-ahead).** Every chord here is a function key, a `Ctrl` chord, or `Delete` — **no bare
  letter** is claimed; the ListView/tab type-ahead reservation is intact.
- **#3 / #38 (single focusable `ListView`, latency).** No second focusable pane is introduced; the
  single sectioned `ListView` model is untouched. Native modals are proven not to regress steady-state
  or intra-modal latency (§4). The sub-context resolution is a pure table lookup on the existing
  keypress path — no new run-loop, no per-keypress allocation beyond today's `OnKey`.

## 8. Acceptance criteria (this slice)

- [x] A design note in `docs/plans/` describing the contextual-chord model, the `F2`/`Ctrl+E`
  relationship, and the modal decision — **this file**.
- [x] Decisions recorded on #538 (maintainer comment) and turned into an implementable model here.
- [x] Honors the invariants: bare letters reserved for type-ahead (#12); no second focusable pane /
  latency regression (#3); the footer reflects the active tab's meaning (§2.4, §6).
- [x] No production code change beyond the note; C–G implement against it.
