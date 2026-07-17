# Plan — Task Detail `Ctrl+E` description editor (#217, H of epic #208)

## Goal (from the issue's acceptance criteria)

- `Ctrl+E` on the Task Detail screen opens a multi-line description editor **pre-filled**
  with the current `TaskDetail.Description`.
- **Save** persists via `SetTaskDescriptionAsync` (#211, already on `main`) and the detail
  **reflects the new text without a manual refresh**.
- **Esc** cancels, with an **inline confirm on unsaved changes** (à la
  `PromptTemplateEditorScreen`'s pending Y/N — no nested modal, per #38).
- **Server failure keeps the editor open** and flashes the error.
- Plain-text (not markdown), matching #211's decision, so open → edit → save → re-read is
  lossless and consistent with how the Description tab renders.
- `dotnet test` green; `tui-validate` confirms open (pre-filled) → edit → save → reflected,
  and cancel.

## Design — mirror the #216 comment composer (inline overlay), not a second screen

The closest precedent is the Ctrl+N comment composer (#216): a bottom-anchored `FrameView`
overlay hosting a multi-line `TextView` + a button row, driven by a **pure** model, with the
ClickUp write injected as an async callback. Reusing that shape keeps the single-`ListView`
focus/latency model (#3) untouched and stays consistent with the epic. **No second focusable
pane, no bare-letter keybinding** — `Ctrl+E` is a chord, and the read-only detail panes never
need Ctrl+E, so pre-empting it is safe (same reasoning as Ctrl+A/N/U).

### Phase 1 — pure model + facade passthrough + unit tests

- New `Tui/Screens/DescriptionEditorModel.cs` (mirrors `CommentComposerModel`):
  - `EditorKey { Save, Cancel, Other }` / `EditorAction { Save, Cancel, PassThrough }` + `Route`.
  - `Seed(string? current) => current ?? ""` — the editor's initial text.
  - `Normalize(string?) => (…).Trim()` — trailing whitespace trimmed; `""` is a **valid**
    value that clears the description (facade sends `""` → ClickUp clears).
  - `IsDirty(string? original, string? current)` — `Normalize(original) != Normalize(current)`.
    Drives the Esc unsaved-changes confirm and lets Save skip a no-op write.
- `TaskService.SetTaskDescriptionAsync` passthrough to `IClickUpClient` (mirrors
  `CreateTaskCommentAsync`).
- Unit tests `DescriptionEditorModelTests` (Route, Seed, Normalize, IsDirty edge cases).

### Phase 2 — TaskDetailScreen overlay + help + host wiring

- `TaskDetailScreen`: add `_descriptionBox` (FrameView), `_descriptionEditor` (TextView,
  `WordWrap`, `TabKeyAddsTab=false`), Save/Cancel buttons, a pending-discard confirm `Label`,
  and a `_setDescriptionAsync` callback ctor param (null ⇒ `Ctrl+E` inert, non-interactive host).
  - `Ctrl+E` in `OnKey` (guarded `!_promptBox.Visible && !_commentBox.Visible && callback != null`)
    opens the editor seeded from `_task.Description`.
  - Extend the top-of-`OnKey` overlay guard to also early-return while `_descriptionBox.Visible`
    (so screen chords don't fire under the editor), mirroring the composer guard.
  - `OnDescriptionKey`: pending-confirm Y/N first (Y discards+closes, else dismiss confirm);
    Tab/Shift+Tab cycle editor↔Save↔Cancel; F1 opens Help without swallowing; then route
    Save (Ctrl+Enter / Save button) and Cancel (Esc) via the model.
  - Save: no-op-close when not dirty; else write off the UI thread. Success → `UpdateData(_task
    with { Description = confirmed }, _comments)` + close + flash; failure → keep open + flash.
    `_disposed` guard on the continuation (mirrors the comment-post path).
- `HelpItemSets.Detail`: add `Ctrl+E edit description`. New `HelpItemSets.DetailDescriptionEditor`
  set; `HelpItems` returns it while the editor overlay is visible.
- `TodoApp`: pass `setDescriptionAsync: (text, ct) => _tasks.SetTaskDescriptionAsync(taskId, text, ct)`.

### Phase 3 — tui-validate harness + drive script

- Harness `Program.cs`: make the task description **mutable** — a `PUT /task/{id}` carrying a
  `description` updates a shared field (JSON-parsed/escaped) that `DetailJson` echoes, so a
  saved description round-trips on the write response and later GETs.
- `description_edit_check.py` (sibling of `detail_comment_check.py`): Enter → detail, `Ctrl+E`
  opens pre-filled, edit text, Tab→Save→Enter, assert the new text shows in the Description tab
  and the editor closed; reopen, edit, Esc → confirm prompt → discard, assert original restored.
- Run the four tui-validate guards (latency, volume, color A/B, detail A/B).

## Hard-rule checks

- No `Generated/` hand edits (facade already exists from #211; no spec change needed).
- Integration boundary is unchanged — no new integration test needed (facade round-trip is
  already covered by #211's `SkippableFact`); logic lives in the pure model + is unit-tested.
- Single sectioned ListView / no second focusable pane; `Ctrl+E` chord, not a bare letter.
