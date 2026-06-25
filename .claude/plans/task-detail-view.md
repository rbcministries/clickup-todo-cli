# Plan — Issue #17: Task detail view on Enter; open-in-browser → Ctrl+B

## Goal / acceptance (from the issue)

- **Keybinding change:** Enter opens a **task detail view**; *open in browser* moves to **Ctrl+B**.
- **Detail view:** header (title, tags, assignees) + a **tabbed, scrollable** pane:
  1. Description (plain/markdown text)
  2. Comments
  3. Other attributes: created / last-activity dates, list, custom fields.
- Esc/Enter close the view; Ctrl+B (from within) opens in browser.
- **API work:** add `GET /v2/task/{task_id}` (single task: description/text_content, tags,
  assignees, date_created/date_updated, custom_fields, list) and
  `GET /v2/task/{task_id}/comment`; regen Kiota; extend `ClickUpClient` + a detail DTO.
- Update help / hint text for the new bindings.

This issue is the dependency for the whole #23 epic (#24 prompt composer and #26 wiring both
consume the detail DTO + comments), so the DTOs are shaped to be rich enough for #24.

## Hard-rule checkpoints

- Edit the **curated spec** `clickup-openapi.json`; never hand-edit `Generated/`. Regen via
  `dotnet kiota generate` (the `regen-client.ps1` body; pwsh is absent in this env so the
  underlying `dotnet kiota` command is run directly with identical args).
- Raw `Authorization` header auth stays untouched.
- Single sectioned `ListView` model in the dashboard is unchanged — the detail view is a separate
  **modal** (`Dialog`), not a second focusable pane in the dashboard. No input-latency regression (#3).
- Command keys stay chords / function keys; Enter/Space/Tab/Esc semantics in the dashboard already
  exist — only the Enter action changes (browser → detail) and Ctrl+B is added.
- Tests land with the code: pure formatter logic is unit-tested; ClickUp-boundary calls are
  `SkippableFact` env-gated.

## Design

### Curated spec (`clickup-openapi.json`)
- Add `get` to existing `/v2/task/{task_id}` path → `operationId: GetTask`, 200 = `Task`.
- Add `/v2/task/{task_id}/comment` `get` → `operationId: GetTaskComments`, 200 = `CommentsResponse`.
- **Extend the shared `Task` schema** (nullable/optional, so list endpoints are unaffected, and
  `date_created` also helps #19): `custom_id`, `text_content`, `description`, `date_created`,
  `priority` (`Priority` obj), `tags` (`Tag[]`), `assignees` (`User[]`), `custom_fields`
  (`CustomField[]`).
- New schemas: `Priority {id,priority,color}`, `Tag {name,tag_fg,tag_bg}`,
  `CustomField {id,name,type}` (value intentionally untyped → ignored; rich value rendering deferred,
  see follow-up issue), `Comment {id,comment_text,user,date,resolved}`,
  `CommentsResponse {comments:[Comment]}`. Reuse `User` for assignees and comment authors.

### Domain DTOs (`ClickUp/Models.cs`)
- `TaskDetail` — id, custom_id, name, url, status (+color), list id/name, description
  (text_content preferred), due/created/updated ms, priority name, `Tags` (names),
  `Assignees` (display names), `CustomFields` (`CustomFieldItem`).
- `CommentItem(Id, Author, DateMs, Text, Resolved)`.
- `CustomFieldItem(Name, string? Type)`.

### Client (`ClickUp/ClickUpClient.cs`)
- `GetTaskDetailAsync(taskId)` → maps `TaskObject` (now richer) to `TaskDetail`.
- `GetTaskCommentsAsync(taskId)` → maps `CommentsResponse.Comments` to `CommentItem[]`.
- Reuse the `Guard` + epoch-parse helpers; add a small `ParseMs` helper shared with `Map`.

### Formatter (`Tui/TaskDetailFormatter.cs`) — pure, unit-tested
- `Header(TaskDetail)`, `Description(TaskDetail)`, `Comments(IReadOnlyList<CommentItem>)`,
  `OtherAttributes(TaskDetail)` → strings. No Terminal.Gui dependency.

### TUI (`Tui/TaskDetailView.cs`) — modal, build-verified only
- `Dialog` with a header `Label`, a `TabView` (Description / Comments / Other) of read-only
  scrollable `TextView`s, and a hint line. Tab cycles tabs; ↑/↓/PgUp/PgDn scroll; Esc/Enter close;
  Ctrl+B returns a "open browser" signal to the caller.
- `TaskDetailView.Show(...)` returns an enum/bool indicating whether to open the browser, so the
  process-launch stays in `TodoApp` (matches existing `OpenInBrowser`).

### Dashboard wiring (`Tui/TodoApp.cs`)
- Enter → load detail + comments off the UI thread (with a "Loading…" flash), then show the modal;
  on the modal's browser signal, call the existing browser launch.
- Add Ctrl+B → `OpenInBrowser()`. Update help dialog + hint bar copy.

## Phases
1. Spec + regen + client + DTOs + formatter + unit tests + integration `SkippableFact`s. Gate
   (build/test/format), commit, push → draft PR.
2. TUI detail view + Enter/Ctrl+B rebinding + help/hint copy. Build-verify, commit, push.
3. Finalize (`gh pr ready`), subagent review, address comments.

## Deferred (file follow-up issues, link from PR)
- Rich custom-field **value** rendering (loosely-typed payloads) — names/types only in this slice.
- Multi-list membership (`locations`) in the Other tab — home list only for now.
