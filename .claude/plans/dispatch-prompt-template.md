# Plan — #100 A3: Editable Dispatch prompt template (placeholders) + editor screen

Part of the #90 agent-dispatch epic. Supersedes #27's single-line `PromptPreamble`
override with a full, editable prompt **template** built from placeholders, editable in a
dedicated full-window editor reached from the F2 settings screen.

Foundation #91 (wiring `AgentDispatch` into the live dispatcher, incl. threading the
preamble through `DispatchAsync`) is **merged on main** — verified: `TodoApp` builds the
dispatcher from `_config.AgentDispatch.ToLauncherOptions()` and passes
`settings.PromptPreamble` into `DispatchAsync` (`TodoApp.cs:855,862`). So the preamble is
currently *live*; migrating a saved value forward is therefore the non-destructive choice.

## Acceptance criteria (from the issue)

- Refactor `AgentPromptComposer.Compose` to render a **template** by substituting
  placeholders. Expose a `DefaultTemplate` whose rendering is **byte-for-byte identical to
  today's output** (regression-guarded).
- Placeholder set: `{userPrompt}`, `{taskJson}`, `{commentsJson}`, `{contextJson}`,
  `{taskId}`, `{customId}` (custom-id → id fallback per #98),
  `{postCommentInstruction}` / `{outputDirInstruction}` (empty until #97/#98 land).
- Preamble is **inline literal text** inside `DefaultTemplate`, not a `{preamble}`
  placeholder.
- Unknown placeholders: **left literal**; `{{` / `}}` escape to literal braces. Documented.
- Persist `string PromptTemplate` on `AgentDispatchSettings` (blank ⇒ `DefaultTemplate`).
  Round-trips via `ConfigStore`; old configs without the key load to default.
- Retire the standalone `PromptPreamble`. **Migrate** a saved non-blank preamble into
  `PromptTemplate` (substitute it for the default preamble line) — flag the drop-vs-migrate
  choice for the maintainer in the PR.
- New editor screen reached from F2: multi-line `TextView` seeded with current template
  (saved or default), Save/Cancel, **Ctrl+Alt+R** reset-to-default behind a Y/N confirm,
  placeholder reference listed at the bottom.
- Pure, unit-tested helpers for template rendering, template normalization, and the
  reset-confirm decision. TUI surface verified by build + reasoning per the repo's TUI rule.

## Design

### Rendering (`AgentPromptComposer`)

- `Render(string template, IReadOnlyDictionary<string,string> values)` — a single
  left-to-right scan: `{{`->`{`, `}}`->`}`, `{known}`->value, `{unknown}`->emitted literally,
  a lone `{`/`}` emitted literally. Substituted values are **not rescanned** (so the JSON
  braces inside `{contextJson}` are never re-interpreted).
- `DefaultTemplate = "{userPrompt}\n\n" + Preamble + "\n\n{contextJson}"` — renders to
  `{prompt}\n\n{Preamble}\n\n{json}`, identical to the pre-#100 `Compose`.
- `Compose(task, comments, userPrompt, string? template = null)` — blank template =>
  `DefaultTemplate`. Builds the values map:
  `userPrompt`=trimmed prompt, `taskJson`=`BuildTaskJson`, `commentsJson`=`BuildCommentsJson`,
  `contextJson`=`BuildJson` (unchanged combined object), `taskId`=`Id`,
  `customId`=`CustomId ?? Id`, `postCommentInstruction`/`outputDirInstruction`=`""`.
- `BuildJson` is **left untouched** (protects the byte-identical `{contextJson}`).
  `BuildTaskJson` / `BuildCommentsJson` are new siblings using the same field shapes
  (comment: keep in sync with `BuildJson`).
- `WritePromptFile(..., string? template = null)` threads the template through.

### Settings (`AgentDispatchSettings`)

- Add `string PromptTemplate { get; set; } = ""`.
- Add deserialize-only shim `LegacyPromptPreamble` (`[JsonPropertyName("promptPreamble")]`,
  ignore-when-null) mirroring `AppConfig.LegacyExcludedStatuses`; remove the live
  `PromptPreamble`.
- `IsDefault` checks `PromptTemplate` blank instead of `PromptPreamble`.

### Migration (`ConfigMigrations`, v2 -> v3)

- Bump `CurrentVersion` to 3. If `SchemaVersion < 3`: when `LegacyPromptPreamble` is
  non-blank and `PromptTemplate` is blank, seed
  `PromptTemplate = AgentPromptComposer.DefaultTemplateWithPreamble(legacy)` (the default
  template with its preamble line swapped). Always null out `LegacyPromptPreamble` so it
  stops being persisted. Version-gated => one-shot.

### Dispatch call site (`AgentDispatcher` / `TodoApp`)

- Rename `DispatchAsync`'s `preamble` parameter to `template`; `Compose`/`WritePromptFile`
  receive `template`. `TodoApp` passes `settings.PromptTemplate`.

### Editor screen (`PromptTemplateEditorScreen` + pure `PromptTemplateEditor`)

- Full-window `Screen`; multi-line `TextView` seeded with the saved template or
  `DefaultTemplate`. Save exposes `Result` (the normalized template, or `null` on cancel).
- Pure `PromptTemplateEditor`: `Normalize(text)` (normalize newlines, strip trailing
  whitespace; text equal to `DefaultTemplate` => `""` so "blank = default" stays clean) and
  `ApplyReset(bool confirmed, string current)` (`confirmed ? DefaultTemplate : current`).
- **Ctrl+Alt+R** -> Y/N `MessageBox` warning it reverts all custom changes; on confirm,
  replace the editor text with `DefaultTemplate`. F1 -> Help, Esc -> cancel.
- Non-focusable placeholder-reference label at the bottom.
- Reached from `SettingsScreen`: a "Edit prompt template..." button raises an event carrying
  the current template + an apply callback; `TodoApp` opens the editor stacked (like Help)
  and, on save, applies the new value back into the settings screen's carried template.
  `SettingsScreen.Save` includes the carried `PromptTemplate` in its `AgentDispatchSettings`
  result, so the F2 Save is the transaction boundary (Cancel discards the edit too).
- Add `HelpItemSets.PromptTemplateEditor`; remove the preamble `TextField` from
  `SettingsScreen`.

## Phases

1. **Core logic + tests** (all CI-testable): composer template refactor, settings field +
   legacy shim + `IsDefault`, migration v3, dispatcher/TodoApp rename. Update the
   preamble-era tests to the template model; add rendering/migration/round-trip tests.
   -> first push opens the draft PR.
2. **TUI: editor screen + F2 wiring**: `PromptTemplateEditorScreen`, pure
   `PromptTemplateEditor` (+ tests), `SettingsScreen` wiring, `HelpItemSets`, `TodoApp`
   `OpenSettings` wiring. Build + reason.
3. **Validate + finalize**: `dotnet format`, `tui-validate` (footer/editor render), mark PR
   ready, review subagent, address review.

## Deferred / coordinated

- `{postCommentInstruction}` / `{outputDirInstruction}` render empty and are **not** placed
  in `DefaultTemplate` yet (would break byte-identity with no text to inject). #97/#98 add
  their instruction text and slot the placeholders into `DefaultTemplate`. Noted in the PR.
- Overlaps the in-flight #93 PR (#139, Dispatch pane) only at the `TodoApp` dispatch call /
  `SettingsScreen` layout; resolved by normal rebase — does not block this issue.
