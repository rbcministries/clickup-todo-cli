# Task Detail Other tab: edit Custom Field values in place (#587 §2/§3)

Status: in flight — implements **§2 + §3** of #587. **§1** (the per-task
Custom Field write path — `SetTaskCustomFieldAsync` / `ClearTaskCustomFieldAsync`
over `POST`/`DELETE /task/{id}/field/{fid}`) already shipped and merged in #596;
this is the Terminal.Gui half that drives it.

**Progress:** Phase 1 (the pure `CustomFieldOtherTabArranger` projection) merged in
#602. Phase 2 (the navigable Other-tab `ListView` row model) is up in PR #620.
**Phase 3 (per-type activation) is the remaining slice**, tracked on #587.

## Problem

On an existing task the Task Detail **Other** tab renders custom fields strictly
read-only: `TaskDetailFormatter.CustomFieldsBody` (#35) is poured into a
word-wrapped `ReadOnly = true` `TextView` inside `Tui/DetailOtherTabView.cs`.
There is no way to change a value from the TUI once a task exists. This adds a
navigable, per-type-activatable custom-field surface, mirroring the
**Checklists tab** interaction model (#456/#457/#458/#572) rather than the New
Task screen's multi-focusable widget stack — which keeps the #3 invariant (no
second focusable pane, no keypress-latency regression) intact.

## Consumed foundations (all on `main`)

- **Write facade (§1, #596):** `ClickUpClient.SetTaskCustomFieldAsync(taskId,
  fieldId, JsonElement value, ct)` and `ClearTaskCustomFieldAsync(taskId,
  fieldId, ct)` + `IClickUpClient` declarations + `TaskService` passthroughs.
- **Definitions (#249, #369):** `GetListCustomFieldsAsync(listId)` →
  `IReadOnlyList<CustomFieldDefinition>` (carries `Id`, `Name`, `Type`,
  `Required`, options). `TaskDetail.ListId` addresses the task's list.
- **Pure helpers (#368):** `CustomFieldValueSerializer.Build(definition, entry)`
  → `CustomFieldWriteResult` (`Skip` / `Value(CustomFieldValue(Id, JsonElement))`
  / `Error(msg)`); `CustomFieldTypes.IsFillable(type)` / `.Fillable`;
  `CustomFieldRequiredValidator`.
- **Read model (#35):** `TaskDetail.CustomFields` is `IReadOnlyList<CustomFieldItem>`
  (`Name`, `Type`, `JsonElement? Value`, `Options`, `Id`);
  `TaskDetailFormatter.CustomFieldValue(item)` renders a value for display.
- **Row-list / overlay patterns:** `ChecklistArranger` (pure projection),
  the Checklists tab view (row `ListView`, Space-toggle, Enter/F2 overlay),
  `DetailOtherTabLayout.Compute` (#81 header/body split + `SpilledHeaderLines`).
- **Contextual-chord model (#538, `contextual-chord-model.md`):** the
  `DetailSubContext` dimension + `ResolveDetail` activation layer in
  `Keybindings.cs`, cross-checked against the footer (#355).

## The read-side gap (resolved in #596's note: lazy-on-first-activation)

`TaskDetail.CustomFields` (`CustomFieldItem`) carries options but **no
`Required` flag**, and `CustomFieldValueSerializer.Build` takes a
`CustomFieldDefinition`. So editing needs the list's definitions via
`GetListCustomFieldsAsync(task.ListId)`, paired to the task's values by field id.
**Decision (per #596 / the issue's suggested default): fetch definitions lazily
on first activation** — cheap, one request, a beat of latency only on the first
edit of a detail open; cached for the rest of that Task Detail session.

## Phasing (commit + push per phase; first push opens the draft PR)

The surface is large and latency-sensitive, so it lands in phases at clean,
green boundaries. If the session can't reach the later phases cleanly, they are
deferred to a follow-up (clearly noted in the PR) and #587 stays open.

### Phase 1 — pure row projection (§2 core, CI-verifiable) — the guaranteed-green slice

A new pure `CustomFieldOtherTabArranger` mirroring `ChecklistArranger` (no
Terminal.Gui, no I/O), separately unit-tested so the #81 short-terminal
guarantee can't regress. It lives in `Tui/` (namespace `ClickUpTodo.Tui`,
colocated with `DetailOtherTabLayout`) rather than `Services/`, because it reuses
`TaskDetailFormatter.CustomFieldLine`/`CustomFieldValue` (in `Tui/`) as the
single source of a field's rendered line — putting it in `Services/` would invert
that dependency:

- **`CustomFieldOtherRowKind`** — `Spill` (a clipped coloured-header line pushed
  into the body by #81, non-selectable), `SectionLabel` (the `Custom fields:`
  heading / empty-state line, non-selectable), `Field` (one custom field,
  selectable when fillable).
- **`CustomFieldOtherRow`** — `readonly record struct` carrying `Kind`, `Text`
  (the display line), `FieldId` (null except on `Field`), `FieldType`,
  `Fillable` (`CustomFieldTypes.IsFillable(type)`), and the current display
  `Value` text. `Selectable => Kind == Field && Fillable`.
- **`CustomFieldOtherTabArranger.Project(IReadOnlyList<string> spilledHeaderLines,
  IReadOnlyList<CustomFieldItem> fields)`** → the flat row list: spill rows
  first (non-selectable), then the `Custom fields:` label, then one row per field
  (ordered stably), or an empty-state row when there are none. Long values are
  truncated in the row with the full value available in the editor overlay
  (Phase 3); no wrapping into continuation rows in this slice.
- The view keeps a projected-row list and moves selection **only over
  selectable rows** (↑/↓ skip non-selectable rows; PgUp/PgDn still page the
  body). `DetailOtherTabLayout` stays the pure split arithmetic and feeds the
  arranger its `SpilledHeaderLines`.

**Tests:** `CustomFieldOtherTabArrangerTests` (pure `Fact`s): spill lines become
leading non-selectable rows in order; the section label is non-selectable;
fillable types are selectable and non-fillable/computed types are rendered but
not selectable; empty custom fields yield an empty-state non-selectable row;
value truncation; stable ordering.

### Phase 2 — the navigable Other-tab row model (§2 UI, tui-validate)

Refactor `DetailOtherTabView` from the opaque `ReadOnly` `TextView` body into a
focusable row `ListView` driven by the Phase-1 projection: ↑/↓ moves selection
over selectable rows, PgUp/PgDn pages, the coloured header attributes
(`DetailAttributesView`) stay non-selectable, and the #81 spilled header lines
render as non-selectable rows at the top of the body. No new focusable pane —
the body `ListView` replaces the body `TextView` one-for-one (single focus
target, as today).

### Phase 3 — per-type activation (§3, tui-validate)

Reusing the checklist patterns, driven through the `DetailSubContext` model:

- Add `DetailSubContext.Other` (the Other tab) to `Keybindings.cs` with its
  active actions, plus new `KeyAction.ToggleCustomField` (`Space`) and
  `KeyAction.EditCustomField` (`Enter`); register footer items and update the
  #355 cross-check (footer ⊇ table per sub-context) — **updated, not weakened**.
- **`checkbox` → `Space`:** toggle in place, optimistic with revert-on-failure +
  flash, via `SetTaskCustomFieldAsync` (serializer `entry.Checked`).
- **text / short_text / url / email / phone / number / currency / date →
  `Enter`:** a single-line editor overlay (mirroring the checklist rename
  overlay's dirty/discard-confirm discipline) pre-filled with the current value;
  on accept, `CustomFieldValueSerializer.Build` parses/validates and its `Error`
  outcome is flashed; a value is written via `SetTaskCustomFieldAsync`; an empty
  submit clears via `ClearTaskCustomFieldAsync`.
- **computed / non-fillable (`formula`, `rollup`, `users`, `tasks`, …):**
  rendered but inert — activating flashes a clear "not editable here" rather than
  opening a no-op editor (`CustomFieldTypes.IsFillable` is false).
- Definitions fetched lazily on first activation (`GetListCustomFieldsAsync`),
  cached for the detail session, paired to values by field id.
- **Required-clear:** an empty submit on a `Required` field is left
  **server-authoritative** — the write is attempted and a 4xx is surfaced/flashed
  (rather than a client-side block), matching the empty-body "always re-fetch"
  shape of the write facade; documented here per the issue's guidance.

**Deferred to a clearly-noted follow-up (tracked on #587) if the surface is too
large to land green this session:** the multi-select `labels` and single-select
`drop_down` **option pickers** (Enter over an option list), and mouse
click-activates-a-row (new wiring on this screen). Phase 3 targets the highest-
value gestures — `Space` toggle and the text-like `Enter` editor — first.

## Hard-rules check

- No `Generated/` hand-edits and **no spec change / Kiota regen** — the write
  path (§1) already exists; this slice is projection + view + keybindings only.
- ClickUp auth quirk untouched.
- Logic in a pure, unit-tested arranger + the keybinding table; no test weakened;
  integration tests (if any) remain `SkippableFact`/env-gated.
- No second focusable pane; the Other-tab body stays a single focus target; the
  sub-context resolution is a pure table lookup on the existing keypress path
  (no per-keypress allocation beyond today's `OnKey`) — #3/#12 intact.

## Verification

- `dotnet build -c Release` (0 warn/0 err), `dotnet test -c Release` (integration
  skips without creds), `dotnet format`.
- `tui-validate`: extend/add an Other-tab check exercising a checkbox toggle and
  a text edit (using `detail_check.py` / `checklist_check.py` as templates) once
  Phase 2/3 land; the projection guarantees (§2, #81) are covered by the pure
  unit tests.
