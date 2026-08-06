# Task Detail (L): @-mention in the description editor (#326)

Epic #313, sub-issue **L**. Depends on **G** (#321, spike — closed), **J** (#324,
`MentionPickerView` — merged) and **K** (#325 / PR #472, mention-in-composer pattern — merged).

## The G verdict decides the shape (#321, Finding 2)

The spike (#321) established that **a real linked @-mention inside a task description is not
expressible via the ClickUp v2 API**: the description is a plain / markdown string
(`UpdateTaskRequest.description`) with no structured-block payload analogous to the comment
`comment` blocks array. A markdown `@name` lands as literal text, not a live / notifying mention.

So this issue takes its **documented fallback branch**: wire the existing #324 mention picker into
the description editor (`Ctrl+E`) so the user can pick a member and splice a visible `@DisplayName`
reference into the text, and **leave the description write path plain-string, unchanged**. The saved
`@name` is a textual reference, not a mention ClickUp resolves/notifies — documented in the footer,
the code, and the PR. No `clickup-openapi.json` change, no Kiota regen.

## Acceptance criteria (from the issue)

- Not supported per **G** → `@name` text is inserted and the limitation is documented; no broken /
  no-op "mention" is shipped.
- Preserve the existing save / discard-confirm / optimistic behavior.
- `dotnet test` green, then `tui-validate` drives `Ctrl+E` → trigger → pick → save.

## Verified current state

- **Description editor** (`Tui/Screens/TaskDetailScreen.cs`): `Ctrl+E` → `ShowDescriptionEditor`
  (`:1991`) over `_descriptionEditor` (`:704`, `TabKeyAddsTab=false`); keys via `OnDescriptionKey`
  (`:1917`) / `ClassifyDescription` (`:1969`); save via `SaveDescription` (`:2040`) through injected
  `_setDescriptionAsync` (`:166`); unsaved-edit discard confirm at `CancelDescriptionEditor`
  (`:2021`); pure `DescriptionEditorModel`. **Description writes are plain-string only** and stay so.
- **Mention picker & overlay already exist (K, #325).** `MentionPickerView` (#324); the transient
  bottom overlay `_mentionBox` / `_mentionPicker` and `ShowMentionPicker` / `HideMentionPicker` /
  `OnMentionPicked` (`:1623`–`:1697`) are built for the **comment composer** — they reference
  `_commentBox` / `_commentEditor` and record a `MentionToken` for the structured comment write.
- **Member pool is already wired in the dashboard host.** `TodoApp` constructs the screen with
  `memberMatch` / `memberTopFrequent` (from K). So the picker's member seams are **already present** —
  the description-mention feature lights up with **no new ctor param and no host wiring**. `SingleTaskApp`
  wires no member seams, so `@` there keeps typing a literal `@` (deferred, tracked by #473).
- **Footer.** `HelpItemSets.DetailDescriptionEditor` (`HelpLine.cs:238`) is the editor overlay footer;
  `DetailFooter` (`:296`) already routes `mentionPickerVisible` → `DetailMentionPicker` ahead of every
  other overlay, and `HelpItems` (`TaskDetailScreen.cs:751`) passes `_mentionBox?.Visible == true`. So
  the shared overlay's footer already covers the description-editor case — only the editor's own footer
  needs a `@` hint added.

## Design

### Generalize the one mention overlay to serve either editor (no second overlay)

The single `_mentionBox` overlay is retargeted, not duplicated:

- New field `TextView? _mentionHostEditor` — the editor the picker currently serves (set on show,
  cleared on hide). New gate `MembersAvailable => _memberMatch is not null && _memberTopFrequent is
  not null` (the picker needs only the member pool — **not** the structured-comment seam, which
  descriptions don't use). The comment path keeps its existing `MentionEnabled` (all three seams) gate
  unchanged, so #325 behaviour is byte-identical.
- `ShowMentionPicker(TextView hostEditor)` — records `_mentionHostEditor`, guards on `MembersAvailable`
  (drops the composer-specific `_commentBox.Visible` guard; both callers are already focus-gated). The
  build/host/size/anchor of the picker is otherwise unchanged.
- `HideMentionPicker()` — refocuses whichever host editor opened it (comment or description, when its
  box is still visible) and clears `_mentionHostEditor`.
- `OnMentionPicked` — inserts the `@Name ` literal into the **host** editor. For the **comment** editor
  it also records a `CommentComposerModel.MentionToken` (the structured comment write, #325). For the
  **description** editor it records nothing: the token is plain literal text (#321 verdict), spliced via
  the shared pure `DescriptionEditorModel.MentionInsertion`.
- The comment `@` trigger calls `ShowMentionPicker(_commentEditor)` (behaviour unchanged).

### Pure model — `DescriptionEditorModel.MentionInsertion` (unit-tested)

```csharp
/// The literal text spliced into the editor for an @-mention: "@" + display name + a trailing space.
/// A description mention is *only* this literal text — ClickUp descriptions carry no structured mention
/// payload (#321), so the saved reference is plain text, never a live/notifying mention.
public static string MentionInsertion(string? displayName) => "@" + (displayName ?? string.Empty) + " ";
```

A unit test locks it to the comment composer's token shape (`MentionToken.Token + " "`) so the two
authoring surfaces splice an identical `@Name ` literal, and confirms `Normalize`/`IsDirty` treat an
`@name`-bearing description exactly like any other text (the write path is unchanged).

### TUI glue — the description `@` trigger

In `OnDescriptionKey`, after the F1 branch and before `Route(ClassifyDescription(...))`, mirror the
composer's `@` handler:

```csharp
if (MembersAvailable && _descriptionEditor.HasFocus && key.AsRune.Value == '@')
{
    key.Handled = true;            // consume the literal @ — the picker inserts the full "@Name" token
    ShowMentionPicker(_descriptionEditor);
    return;
}
```

Feature off (no member pool, e.g. single-task mode) ⇒ `@` falls through and types a literal `@`, as
today. `HideDescriptionEditor` also calls `HideMentionPicker()` (mirroring `HideCommentComposer`) so a
picker left open can never outlive its editor.

### Footer

Add `new("@", "mention", IsAction: false)` to `HelpItemSets.DetailDescriptionEditor`, exactly as K did
for `DetailCommentComposer` (`IsAction:false` — a typed character, not a re-raisable chord). No
`Keybindings`-table change (composer/editor-internal keys aren't table-routed; the #355 cross-check
covers mapped chords only). Update the `DetailMentionPicker` doc comment to note it now overlays the
description editor too.

## Phases

1. **Pure model + tests** — `DescriptionEditorModel.MentionInsertion`; xUnit (shape + consistency with
   the composer token + `Normalize`/`IsDirty` unaffected by `@name`). Footer `@` hint + `HelpLine`
   tests. Build + test green. First push opens the draft PR.
2. **TUI glue** — generalize the mention overlay (`_mentionHostEditor`, `MembersAvailable`,
   `ShowMentionPicker(host)`, `HideMentionPicker`, `OnMentionPicked` branch), the description `@`
   trigger, `HideDescriptionEditor` picker teardown. Build + test green.
3. **E2E + tui-validate** — `description_mention_check.py` (Ctrl+E → `@` → search → Enter → assert
   `@Ada Lovelace` in the editor → save → assert it renders in the Description body); run `tui-validate`.
   Mark PR ready.

## Non-goals / deferred

- **Real linked description mentions** — not API-expressible (#321, Finding 2). If ClickUp later adds a
  structured description payload this is revisited; nothing here is broken/no-op in the meantime.
- **Single-task-mode description mentions** (`SingleTaskApp` has no member pool) — light up
  automatically once that host supplies the member seams, tracked alongside #473.
- **@Brain / Super Agents** — not offered by the #324 picker (per #321); human members only.
