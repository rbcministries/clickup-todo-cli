# Plan — Contextual chords (F): contextual `Delete` + confirmation modal (#543)

Slice **F** of the contextual key/chord remapping epic (#537). Implements the
`Delete` half of the sub-context model recorded in slice **A**
(`docs/plans/contextual-chord-model.md`). Depends only on **A** (design, merged),
the **#404 native-modal spike** (GO, merged) and its **#554** focusable-form
follow-up (GO), and **C** (#540 / PR #586, merged) which landed the
`DetailSubContext` + `ResolveDetail` activation seam this slice consumes.

## Goal (from #543)

- A `Delete` key that removes the highlighted thing on the **Checklists** tab —
  the item on an item row, the whole group (and its items) on a header row —
  always **behind a confirmation**, replacing #458's `F9` + inline `Enter`/`Esc`
  confirm. The optimistic `Remove` + revert + `SelectAfterDelete` logic from #458
  is reused unchanged; only the **trigger key** (`F9` → `Delete`) and the
  **confirmation surface** change.
- **No `F9` binding remains** anywhere.
- The footer shows `Del 🗑 delete` (was `F9 🗑 delete`); `F8 ✏ rename` is
  untouched (it moves to `F2` in slice D).
- `#355` cross-check green; `tui-validate` green; the confirmation surface
  respects the `#3` latency invariant.

## The confirmation surface — reconciling the issue with the A note

The issue asks the inline confirm be "replaced by the confirmation surface chosen
in A"; slice A chose **native Terminal.Gui modals** (§4). But A also records the
operative caveat from the #404/#554 spikes: *keep the native path behind the
`CLICKUP_TODO_NATIVE_MODAL` flag until the `windows` and `dotnet` drivers are
confirmed (the `tui-validate` harness is ANSI-only), then flip the default. F
owns the promotion — it lands the first real modal; G reuses it.*

So this slice:

- **Promotes** the flag-gated `NativeModalSpike` shape (nested
  `Application.Run(dialog)`, dispose routed through
  `TuiTeardown.DisposeSwallowingTeardownBug`, a single-slot open guard) into a
  small **reusable `ConfirmDialog`** helper — the infrastructure F "owns" and G
  will reuse for its sibling-vs-child choice dialog.
- Wires `Delete` on the Checklists tab to that confirmation:
  - **Flag on** (`CLICKUP_TODO_NATIVE_MODAL` set) → the native `ConfirmDialog`.
  - **Flag off** (default) → the proven **inline `Enter`/`Esc` armed confirm**
    from #458, now triggered by `Delete` instead of `F9`.

Both paths are "an explicit confirmation"; the default stays the driver-safe
bespoke confirm during the driver-validation window the A note defines, while the
native surface lands, exercised ANSI-side by `tui-validate`, ready for the
eventual default-flip and for slice G.

## Design

### Keybindings.cs

- **Retarget** `DeleteChecklistItem`: token `F9 → Delete`. The Checklists
  sub-context activation set already lists `DeleteChecklistItem` (added in C), so
  no activation-table change is needed; only the base-`Map` token moves.
  `AllBindingsOfAnAction_ShareOneKey` still holds — the action keeps a single
  token. The anti-collision invariant holds — `Delete` is new and distinct within
  every sub-context.

### HelpLine.cs

- In `Detail` and `DetailWithTaskTree`, the `new("F9", "🗑 delete")` item becomes
  `new("Del", "🗑 delete", Chord: "Delete")` — `ActionKey` = `"Delete"`, matching
  the retargeted token and the `Del`/`Delete` display style already used by the
  Filter·Sort·Group and Dispatch-providers footers. `F8 ✏ rename` unchanged.

### ConfirmDialog.cs (new, `Tui/`)

- A reusable native confirm modal: title + message + **Cancel** (default, so a
  stray `Enter` cancels) / **Delete** buttons; `Esc` cancels. Runs on a nested
  `Application.Run(dialog)`, disposed through the shared teardown guard, guarded
  by a single open-slot so a buffered double-press can't stack two loops.
  Invokes `onResult(true/false)` in the `finally` after the loop returns.
- `Enabled` mirrors `NativeModalSpike.Enabled` (same env gate) — off by default.

### TaskDetailScreen.cs (hand-rolled `OnKey`, not migrated to the dispatcher)

- Split the `F8/F9` OnKey block: **keep `F8`** (rename item / group by row-kind),
  **drop `F9`**.
- Add a **`Delete`** block beside the other Checklists-tab guards, routed through
  `Keybindings.ResolveDetail(CurrentDetailSubContext(), "Delete") ==
  DeleteChecklistItem` (so dispatch and the per-tab footer stay in lock-step, like
  the `Ctrl+N` block). On a header row → group delete; on an item row → item
  delete.
- `DeleteSelectedChecklistItem()` / `DeleteSelectedChecklistGroup()` branch on
  `ConfirmDialog.Enabled`: flag on → open the native `ConfirmDialog`, calling
  `PerformChecklistItemDelete` / `PerformChecklistGroupDelete` on confirm and
  flashing "Delete cancelled." on cancel; flag off → arm the existing inline
  confirm (unchanged). The inline-confirm answering block in `OnKey` is untouched
  (only reached on the flag-off path).

### Tests (KeybindingsTests.cs)

- The parametric cross-checks (`Footer_ShowsTheTableKey_ForEveryBinding`,
  `EveryToken_IsParseable`, `DetailFooter_PerSubContext_ShowsEveryLiveBinding`,
  the anti-collision invariant) pick up the retarget automatically once the footer
  carries `Del`/`Delete`.
- Add a named pin `DeleteChecklistItem_IsDelete_AndNoBindingUsesF9`, mirroring
  `AddChecklistItem_IsCtrlN_AndNoBindingUsesF7`, so a later slice can't resurrect
  `F9`.

## Scope & deferral (#543 "scope per what's available, defer the rest with a note")

- **In scope:** the Checklists-tab `Delete` (item + group), the reusable
  `ConfirmDialog`, and the `F9 → Delete` retarget.
- **Deferred:** `Delete` for **comments** (needs a `DeleteCommentAsync` facade +
  a curated-spec check — no comment-delete facade exists today) and for
  **tasks / subtasks** from Task Detail (the `DeleteTaskAsync` facade exists but
  is unwired; deleting the task you're viewing raises a navigation-after-delete
  question that belongs with the main-list `Delete`). Tracked in a follow-up issue
  linked from the PR.

## Verification

- `dotnet build -c Release` (0 warn/0 err), `dotnet test -c Release` (integration
  skips without creds), `dotnet format --verify-no-changes`.
- `tui-validate`: a `checklist_check.py` delete step now via `Delete` (not `F9`),
  default (inline-confirm) path — item deleted after `Enter`, cancelled on `Esc`;
  and the native path behind the flag exercised as the spike checks do. No second
  focusable pane / latency regression (#3).
