# Checklist-item F2: relabel "Rename" → "Edit" (#601)

Cosmetic follow-up split out of **#541** (slice D of the contextual-chord epic
**#537**), which retargeted the checklist item/group edit chord `F8 → F2` — *only
the trigger key*, per #541's stated scope. Tracked in **#601**.

## Why this is decision-free

`docs/plans/contextual-chord-model.md` already ratifies the framing, twice:

- **§2** (lines 199–200): *"The `F2` label is **Edit** wherever the surface edits
  more than a title (checklist item, and the `Ctrl+E`-alias tabs) and **Rename**
  only where a title is the sole editable facet (Task Tree)."*
- **§3 / §5-D**: because **#572** put a per-item **assignee** control inside the
  same modal as the name field, the one `F2` gesture edits **name + assignee**, so
  *"the honest framing is **Edit**, not Rename"* — and it directs *"consider
  renaming the action `RenameChecklistItem → EditChecklistItem`."*

Since #572 shipped the assignee-in-modal, the checklist `F2` surface now edits
more than a title, so the model's condition is met. This slice executes the
documented model; it does **not** introduce a new decision. `F2` stays the
trigger — no key, binding, or behavior change.

## Scope

Naming/labeling only, matching the model's ratified vocabulary:

- **Footer hint** — `F2 ✏ rename → F2 ✏ edit` in `HelpItemSets.Detail` and
  `HelpItemSets.DetailWithTaskTree` (`HelpLine.cs`). The **main-list** `F2 ✏ rename`
  (`HelpItemSets.MainList`) is **unchanged** — that surface edits a task **title
  only**, so per §2 it stays *Rename*.
- **Overlay titles** — `"Rename item"` → `"Edit item"`, `"Rename checklist"` →
  `"Edit checklist"` (`TaskDetailScreen.cs`), and the `checklist_check.py`
  assertions that pin them.
- **Action / handler rename** (the model's §3/§5-D "consider"):
  - `KeyAction.RenameChecklistItem` → `KeyAction.EditChecklistItem`
    (`Keybindings.cs`, its binding, its `Checklists` sub-context activation, and
    the `Detail` dispatch in `TaskDetailScreen.cs`).
  - The Task-Detail gesture/apply methods → `Edit…`:
    `RenameSelectedChecklistItem`/`RenameSelectedChecklistGroup` and the private
    optimistic-apply `RenameChecklistItem`/`RenameChecklistGroup`.
  - `KeybindingsTests` / `HelpLineTests` names + assertions updated to match.

## Deliberately unchanged (boundary)

- The **`ClickUpClient` facade** `RenameChecklistItemAsync` / `RenameChecklistAsync`
  (and the delegate fields wrapping them) — these are the ClickUp **write path**
  and genuinely *rename* (set the name via `PUT /checklist/{id}/checklist_item/{id}`),
  so the name stays accurate. Out of #601's stated file list, and touching them
  would drag in `IClickUpClient` / `TaskService` / the integration suite for no
  gain.
- The internal `ChecklistItemEditKind` enum (`Rename` / `RenameGroup`) — an
  edit-surface discriminator, not the user-facing action; left coherent as-is.
- The `RenameChecklistItem_SendsPutWithName_AndMapsResponse` facade unit test —
  it exercises the rename API, which keeps its name.

## Verification

- `dotnet build -c Release` (0/0), `dotnet test -c Release`, `dotnet format`.
- `tui-validate` `checklist_check.py` — the item CRUD, group-rename, and
  assignee legs assert the overlay now reads **Edit item** / **Edit checklist**.
- No second focusable pane, no keybinding change (`F2` unchanged) — #3/#12 intact.

Closes #601.
