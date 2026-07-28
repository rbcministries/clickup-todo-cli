# Task Detail: composer-aware footer help set (#436)

## Problem

`TaskDetailScreen.HelpItems` only special-cases the **description editor** overlay
(`_descriptionBox.Visible → HelpItemSets.DetailDescriptionEditor`). While the
**comment composer** (`_commentBox`, opened with `Ctrl+N`) is open there is no
dedicated footer set, so the screen keeps rendering the full `Detail` /
`DetailWithTaskTree` command footer — advertising `Ctrl+A ✨Dispatch`,
`Ctrl+N ➕Comment`, `Ctrl+E ✏Description`, `Ctrl+O 🗁 by ID`, `Ctrl+U`, etc.

Those chords are inert to a **keypress** while composing (`OnKey` returns early at
`TaskDetailScreen.cs:779` when `_commentBox.Visible || _descriptionBox.Visible`),
so the keyboard behaviour is already correct. The gap is the **mouse**: footer
action hints are clickable and re-raise their chord via
`Application.RaiseKeyDownEvent` (#289). With the composer focused, clicking e.g.
`Ctrl+↩ new tab` re-raises `Ctrl+Enter`, which the composer treats as **Post** — a
surprising outcome for a hint reading "new tab". This violates the
"footer advertises it ⇔ the key does something" invariant, but only for a footer
click while the comment composer is open.

## Fix

Mirror the description-editor treatment for the composer:

1. Add `HelpItemSets.DetailCommentComposer`, mirroring
   `DetailDescriptionEditor` but with the composer's own keys. The composer's
   `FrameView` title is the source of truth: *"New comment — Ctrl+Enter or
   Tab→Post · Esc cancel"*, and F1 opens Help even while composing
   (`OnCommentKey`, `TaskDetailScreen.cs:1215`).

   ```
   Tab        editor/Post/Cancel
   Ctrl+Enter post
   F1         ℹ
   Esc        cancel
   ```

2. Extract the detail-footer selection into a pure, unit-testable helper
   `HelpItemSets.DetailFooter(commentComposerVisible, descriptionEditorVisible,
   hasTaskTree)` so the branch order is covered by xUnit (the property itself
   lives on a Terminal.Gui `View` and isn't CI-testable). `HelpItems` becomes a
   one-line delegation to it. The two overlays are mutually exclusive, but the
   composer branch is checked first for clarity.

## Testability

The per-branch selection is pure logic → `HelpLineTests` gets cases pinning:
composer-visible → `DetailCommentComposer`; description-visible →
`DetailDescriptionEditor`; tree present → `DetailWithTaskTree`; otherwise →
`Detail`. Plus a `Format` pin of the new set's rendered footer string, matching
the existing per-set pins in `HelpLineTests`.

## Verification (TUI, not CI)

Build must stay 0/0. Manually / via `tui-validate`: open a task, press `Ctrl+N`
to open the composer, confirm the footer switches to
`Tab editor/Post/Cancel · Ctrl+Enter post · F1 ℹ · Esc cancel` and that clicking
where a command hint used to be no longer re-raises `Ctrl+Enter` as a Post.

## Scope / non-goals

- No keyboard-behaviour change — the early-return guard already makes the command
  chords inert to keypresses while composing; this only fixes the footer's
  advertised affordance (and thus the mouse re-raise leak) during compose.
- Pre-existing bug, not introduced by #384/#434; the description-editor overlay
  already had its own set, this closes the symmetric gap for the composer.
