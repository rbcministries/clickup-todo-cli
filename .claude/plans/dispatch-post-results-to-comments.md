# D5 — Dispatch pane "Post results to Comments" toggle (#97)

Part of the #90 agent-dispatch epic. Depends on #93 (Dispatch pane) and #91 (config wiring),
both **closed**; the pane and `DispatchRequest` shape are settled on `main` after #152/#164/#165
merged.

## Goal

Let the user opt in, **per dispatch**, to having the dispatched `claude` agent post a brief summary
comment back to the ClickUp task at the end of its turn. The app does **not** post the comment —
it only appends an instruction to the seed prompt telling the agent to post one using its own
ClickUp MCP tools. So there is **no ClickUp API change and no Kiota regen**.

## Current state (already scaffolded)

- `AgentPromptComposer` already declares a `postCommentInstruction` placeholder in `Placeholders`
  and seeds it as **empty** in `Compose` (a "toggle lands later" stub). It is **not** referenced by
  `DefaultTemplate` yet, so enabling it must both fill the value and put the token in the default
  template.
- The Dispatch pane (`TaskDetailScreen`) already has a `_postToCommentsToggle` `CheckBox`, labelled
  "Post results to Comments (coming soon)", wired into `_dispatchControls` and the pane layout — but
  it is inert (never read on submit).
- `DispatchRequest` carries `Prompt`, `SessionMode` (#94), `WorkingDirectory` (#95) and is documented
  as "post-results-to-Comments (#97) extends this record next".

So D5 is a **thread-the-flag** change across five files — no new controls, no geometry change.

## Design

### Instruction text & id

The ClickUp MCP comment tools key on the **raw task id**, so the instruction substitutes
`task.Id` (not the custom id). Text (own paragraph, trailing blank line, matching the
`{outputDirInstruction}` precedent):

> `When you have finished, post a brief summary comment on ClickUp task {id} describing what you did (requires ClickUp MCP tools with access to this workspace).`

When the toggle is off the placeholder renders empty, keeping zero-config dispatch byte-for-byte
identical.

### Ordering (well-defined, per the issue's ask)

`DefaultTemplate` becomes:

```
{userPrompt}\n\n{outputDirInstruction}{postCommentInstruction}<Preamble>\n\n{contextJson}
```

So the rendered order is: **user prompt → output-dir instruction (if any) → post-comment
instruction (if any) → preamble → JSON body**. Both instruction paragraphs carry their own trailing
`\n\n` and are empty when off, so with neither enabled the output is byte-identical to today.

Interplay with a custom `PromptPreamble` (#27→#100): `DefaultTemplateWithPreamble` only swaps the
`Preamble` text and leaves the `{postCommentInstruction}` token in place, so the instruction still
renders ahead of the custom preamble — ordering stays well-defined.

A fully custom user template (#100) that omits `{postCommentInstruction}` opts out of the
instruction even when the toggle is on — same contract as `{outputDirInstruction}`. Noted in the PR.

## Changes

1. `Agent/AgentPromptComposer.cs`
   - Add `{postCommentInstruction}` to `DefaultTemplate` (after `{outputDirInstruction}`).
   - New `internal static string PostCommentInstruction(bool enabled, string? taskId)`.
   - `Compose` / `WritePromptFile` gain `bool postToComments = false`; seed the placeholder value.
2. `Agent/AgentDispatcher.cs` — `DispatchAsync` gains `bool postToComments = false`, threaded to
   `WritePromptFile`.
3. `Tui/Screens/DispatchRequest.cs` — add `bool PostToComments = false`.
4. `Tui/Screens/TaskDetailScreen.cs` — read the toggle on submit into `DispatchRequest`; relabel it
   "Post results to Comments (requires ClickUp MCP access)"; drop the "coming soon" / stub comments.
5. `Tui/TodoApp.cs` — `DispatchAgent` reads `request.PostToComments` and passes it to `DispatchAsync`.

## Tests

- `AgentPromptComposerTests`
  - `PostCommentInstruction` on → paragraph present with the task id, positioned before the preamble
    and JSON; off → empty; default-template render places it correctly; combines with output subdir
    and with a custom preamble in the defined order; toggle off keeps byte-identical output.
  - Update the existing `Compose_ToggleInstructionPlaceholders_RenderEmpty…` test (postComment now
    has a live path — keep the empty-when-off assertion) and the `DefaultTemplateWithPreamble` /
    default-layout assertions for the new token (deliberate layout change, not a weakening).
- `AgentDispatcherTests` — `DispatchAsync` threads `postToComments` to the composed file; default is
  off (byte-identical to today).
- TUI toggle wiring (submit reads the checkbox) is Terminal.Gui glue — verified by build + reasoning
  per the repo's TUI rule; the pane has no new focusable control and no geometry change (#3 model
  intact), and no bare-letter shortcut is added (#12).

## Out of scope / deferred

- Running the one-off dispatch as a background child process with an in-TUI "thinking" indicator and
  rendered output is **#99** (D6), unchanged by this PR.
