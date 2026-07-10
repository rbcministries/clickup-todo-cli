# Plan — #97 D5: Dispatch pane "Post results to Comments" toggle

Part of the #90 epic. Depends on #93 (Dispatch pane — merged) and #91 (config wiring — merged),
both `CLOSED`. Blocking dispatch-pane PRs #152/#164/#165 all merged to `main`.

## Goal / acceptance

Let the user opt in, **per dispatch**, to having the dispatched agent post a summary of its work
back to the ClickUp task as a comment. The app does **not** post the comment itself — it only
appends an instruction to the composed prompt telling the agent to do so (which requires the agent
to have ClickUp MCP tool access to the workspace).

Acceptance signals (from the issue):
- A toggle in the Dispatch pane (#93), reachable via Tab, default = **off**. Seeded from the
  persisted `AgentDispatchSettings.DefaultPostResultsToComments` (already exists + editable in F2).
- When **on**, an instruction line is appended to the prompt **before the JSON body** (part of the
  user-prompt/instruction section), with the real task id substituted, e.g.
  `Post a brief summary comment to ClickUp task <taskId> at the end of this turn explaining your work.`
- The flag threads through the dispatch payload
  (`DispatchRequest` → `TodoApp.DispatchAgent` → `AgentDispatcher.DispatchAsync` →
  `AgentPromptComposer.Compose`).
- A UI hint near the toggle notes it requires the dispatched agent to have **ClickUp MCP tool
  access to this workspace**.
- Composer unit test: flag on → the instruction line appears (before the JSON), correct id
  substituted; flag off → prompt byte-identical to today. Interplay with a custom template /
  preamble (#100) is well-defined via the existing `{postCommentInstruction}` placeholder.

## Current state (scaffolding already in place)

- `AgentPromptComposer` already reserves a `{postCommentInstruction}` placeholder (currently always
  `string.Empty`) and lists it in `Placeholders`. It is **not** referenced by `DefaultTemplate` yet.
- `AgentDispatchSettings.DefaultPostResultsToComments` exists; the F2 `SettingsScreen` already reads,
  toggles, and persists it; `IsDefault` accounts for it.
- `TaskDetailScreen._postToCommentsToggle` exists as an **inert stub** ("Post results to Comments
  (coming soon)"), already in `_dispatchControls` / added to the pane / key-routed.

So this issue activates the toggle end-to-end. The task id (not custom id) is used, because the
ClickUp MCP comment tools key on the raw task id.

## Design

Mirrors the #94 one-off toggle (`dispatch-one-off-toggle.md`). The Agent layer stays free of
Configuration: the composer takes a plain `bool postToComments` and builds the instruction from the
`TaskDetail` it already has; the `DefaultPostResultsToComments → per-dispatch bool` seeding happens
at the Tui boundary.

### Phase 1 — Composer: instruction value + template slot (+ tests)

- `AgentPromptComposer.Compose` / `WritePromptFile`: add trailing `bool postToComments = false`
  (optional ⇒ existing call sites/tests unchanged). When true, the `postCommentInstruction`
  placeholder value becomes a "post a summary comment to ClickUp task {id}" paragraph (trailing
  `\n\n`, mirroring `OutputDirInstruction`); when false it stays `string.Empty` (byte-identical
  output).
- Add `{postCommentInstruction}` to `DefaultTemplate` between `{outputDirInstruction}` and the
  preamble, so the default layout carries the instruction when the flag is on and is unchanged when
  off. New `internal static string PostCommentInstruction(TaskDetail)` builds the line.
- `AgentDispatcher.DispatchAsync`: add `bool postToComments = false`, pass to `WritePromptFile`.
- Tests (`AgentPromptComposerTests`, `AgentDispatcherTests`): flag on → instruction present with
  correct id, positioned before the JSON, combines with output-subdir + custom preamble in a
  well-defined order; flag off → byte-identical to no-arg; `postToComments` plumbed through
  `DispatchAsync` (FakeLauncher captures the written file content). Update the one
  `DefaultTemplateWithPreamble` template-string assertion for the new slot.

### Phase 2 — Tui plumbing: payload carries the flag, pane toggle goes live, TodoApp threads it

- `DispatchRequest`: add `bool PostToComments = false` (optional ⇒ `new DispatchRequest(text)` stays
  valid).
- `TaskDetailScreen`: constructor gains `bool defaultPostToComments = false`; seed the toggle
  (`CheckBox.Value`); replace "(coming soon)" wording with real text; add a dim hint label noting the
  agent needs ClickUp MCP access. `SubmitDispatch` reads the toggle into the `DispatchRequest`.
- `TodoApp`: build the screen with `_config.AgentDispatch.DefaultPostResultsToComments`;
  `DispatchAgent` reads `request.PostToComments` and passes it to `DispatchAsync`.

TUI is not CI-testable; verified by build + reasoning (and `tui-validate` after `dotnet test` green).
The single sectioned ListView / no-second-focusable-pane rule (#3) is untouched — the toggle is an
existing control inside the already-built Dispatch pane; only its wording/hint and liveness change.

## Out of scope / deferred
- The app posting the comment itself via the ClickUp API (the design is deliberately "instruct the
  agent"; no new API surface, no spec/regen).
- #99 (one-off background execution + in-TUI output) — the post-to-comments instruction applies to
  whichever session mode is chosen and needs no coordination with #99.
