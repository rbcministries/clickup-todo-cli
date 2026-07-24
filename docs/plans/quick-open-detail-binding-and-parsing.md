# Quick-open (Ctrl+O) follow-ups — detail binding + input-parsing niceties (#353)

Follow-ups deferred from #303 (shipped in PR #352, merged). None block #303; they are
small, independent refinements to the Ctrl+O quick-open feature. Item 4 of #353
(Ctrl+Enter → new-tab variant) stays with the multi-tab work (#301) and is **out of
scope** here.

Base facts (verified in `main`):

- `Services/QuickOpenParser.cs` — pure `Parse`/`FindInCache`; `QuickOpenRef(Kind, Value)`.
- `Tui/TodoApp.cs` — `OpenQuickOpen()` (list-initiated, guarded on `ActiveScreen`),
  `ResolveAndOpen(text)`, `OpenTaskDetail(string taskId)`.
- `Tui/Screens/TaskDetailScreen.cs` — owns its own key handling (`OnKey`), raises host
  events (`QuickUpdatesRequested`, `RefreshRequested`, `AgentDispatchRequested`).
- `ClickUp/ClickUpApiException.cs` — carries `int StatusCode`.
- `ClickUpClient.GetTaskDetailByCustomIdAsync(customId, teamId)` resolves a custom id via
  `custom_task_ids=true&team_id=…` and returns the task with its **real** id.

## Item 1 — Bind Ctrl+O from the Task Detail view

Add quick-open from an open `TaskDetailScreen` so you can jump between tasks without
Esc-ing back to the list first.

- `TaskDetailScreen`: new `QuickOpenRequested` event; a `Ctrl+O` chord in `OnKey`
  (mirroring the `Ctrl+U` `QuickUpdatesRequested` shape — inert while the Dispatch
  prompt / comment composer / description editor own the keyboard).
- `TodoApp`: wire `screen.QuickOpenRequested` to a shared entry-surface opener that
  works whether the list or a detail screen is front-most. **Stack behaviour:** mirror
  Ctrl+U — the quick-open surface stacks over the current detail; on resolve, the new
  Task Detail opens over the current one (Esc walks back one screen at a time). This
  needs no dependency on the in-flight #291 detail→detail "replace-in-place" work.
- Refactor `OpenQuickOpen()` into a `ShowQuickOpenSurface()` core shared by the list
  entry point (keeps its `ActiveScreen is null` guard) and the detail entry point.

## Item 2 — Custom-id URL: use the URL's own `team_id`

A `…/t/{team_id}/{custom_id}` URL currently resolves the custom id against
`config.WorkspaceId`, so pasting a URL from a **different** workspace 404s.

- `QuickOpenRef` gains an optional `string? TeamId` (null for everything except a
  custom-id URL that carried its own team segment). `QuickOpenRef.Custom(id, teamId)`.
- `QuickOpenParser.FromTaskPath` populates `TeamId = segments[0]` on the
  `/t/{team}/{custom}` shape.
- `ResolveAndOpen` prefers `reference.TeamId` over `_config.WorkspaceId`
  (`teamId = reference.TeamId ?? _config.WorkspaceId`); the "no workspace configured"
  guard fires only when the resolved team id is blank.

## Item 3 — Hyphenless bare custom ids (404 fallback)

A hyphenless bare token (e.g. `PROJ123`) parses as `TaskId`; an **uncached** one takes
the plain `GET /task/{id}` path and 404s. Fallback: on a plain-id 404, retry as a custom
id against the workspace team id.

- `TaskService.GetTaskDetailWithCustomIdFallbackAsync(idOrCustomId, teamId, ct)` (pure
  orchestration over the client, unit-tested with a fake `IClickUpClient`): try
  `GetTaskDetailAsync`; on a `ClickUpApiException` with `StatusCode == 404` **and** a
  non-blank `teamId`, retry `GetTaskDetailByCustomIdAsync`; otherwise let the error
  propagate. The common valid-plain-id case still costs one call.
- `OpenTaskDetail(string taskId, string? customIdFallbackTeamId = null)` uses the
  fallback fetch for the detail, then loads comments / wires the composer + editor by the
  **resolved** `detail.Id` (identical to today for a real id; correct for a
  fallback-resolved custom id). The optional param defaults to null, so every existing
  caller (cache hit, feed, double-click, custom-id resolve) is unchanged.
- `ResolveAndOpen`'s uncached plain-id branch passes `customIdFallbackTeamId: teamId`.

## Tests

- `QuickOpenParserTests` — `TeamId` populated on `/t/{team}/{custom}` URLs, null on bare
  custom ids / plain-id URLs / bare ids.
- New `TaskServiceQuickOpenFallbackTests` — plain-id success (no fallback call); plain-id
  404 + team id → custom-id retry; plain-id 404 + blank team id → error propagates;
  non-404 error never falls back.
- `HelpLineTests` — repin the `Detail` set to include `Ctrl+O open by id`.
- The Ctrl+O key binding and detail→detail stacking are Terminal.Gui glue; verified by
  build + `tui-validate` (extend/add an E2E scenario that opens the surface from an open
  detail). TaskDetailScreen composites aren't unit-tested per CLAUDE.md.

## Invariants

- No `Generated/` hand-edit, no curated-spec change — the custom-id endpoint already
  exists (#303); this is parser + service + host glue only.
- No second focusable pane (#3/#38) — the entry surface is the existing full-window modal.
- Bare letters reserved for type-ahead (#12) — Ctrl+O is a chord.
