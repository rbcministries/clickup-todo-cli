# Plan — S1: Agent-dispatch prompt composer & context shaping (issue #24)

Part of the **#23** epic ("Dispatch an interactive `claude` session from the task
detail view"). This is **S1** only: compose the prompt that seeds the dispatched
`claude` session and write it to a temp file. S2 (#25, the terminal launcher) is
already merged (PR #42); S3 (#26, the `A`-keybinding + detail-view wiring) and S4
(#27, the settings surface) stay in their own issues.

The epic gates S1 on **#17** (the detail DTO + comments fetch), which merged in
PR #33 — so `TaskDetail` / `CommentItem` already exist on `main`, and
`TaskDetail` was explicitly "shaped to also seed the agent-dispatch prompt
composer (#24)".

## Acceptance criteria (from the issue)

- New `AgentPromptComposer` taking the detail DTO + comments and the user prompt;
  returns the composed string.
- Assemble in this order: **user prompt** → blank line → **preamble**
  (`"JSON below has task details and comment history; use MCP tools if more
  detail required."`) → blank line → a **single JSON object**
  `{ "task": {…}, "comments": [...] }`.
  - **`task`** (simplified subset, to keep the prompt tight): `id`, `custom_id`
    (if present), `name`, `status`, `list` (`{id,name}`), `url`, `due_date`,
    `priority`, `assignees`, `tags`, truncated `description`.
  - **`comments`**: the **full** comment objects from #17's fetch (`id`, `author`,
    `date`, `text`, `resolved`).
- Serialize with `System.Text.Json`. Write to a temp file under the OS temp dir
  (`<temp>/clickup-todo/agent-prompt-<task>-<unique>.txt`); return the path.
  Keeping the JSON in a file (not on the command line) is what makes launching
  safe (the launcher reads it at run time via `Get-Content -Raw` / `$(cat …)`).
- Decide retention.
- Unit-testable in isolation (no API).

## Design

Lives in the existing `src/ClickUpTodo/Agent/` folder, namespace
`ClickUpTodo.Agent`, next to the launcher it feeds.

### Single input source: `TaskDetail`

The issue lists "the `TaskItem`, the detail DTO + comments". `TaskDetail` is a
**superset** of `TaskItem` for every field the `task` subset needs (`id`,
`custom_id`, `name`, `status`, `list`, `url`, `due_date`, `priority`,
`assignees`, `tags`, `description`), so the composer takes **`TaskDetail`
only** — no redundant `TaskItem` parameter. This matches the DTO's documented
purpose ("shaped to also seed the prompt composer").

### `AgentPromptComposer` (pure + a thin file writer)

```
public const string Preamble = "JSON below has task details and comment history; use MCP tools if more detail required.";
public const int    MaxDescriptionLength = 2000; // truncate to keep the prompt tight

// Pure, no I/O — the core, heavily unit-tested.
public static string Compose(TaskDetail task, IReadOnlyList<CommentItem> comments, string userPrompt);

// Best-effort write: ensures <directory>/ exists, writes UTF-8, returns the full path.
public static string WritePromptFile(TaskDetail task, IReadOnlyList<CommentItem> comments,
                                     string userPrompt, string? directory = null);

internal static string BuildJson(TaskDetail task, IReadOnlyList<CommentItem> comments); // tested directly
```

- **Layout** (deterministic `\n`, cross-platform stable for tests):
  `"{userPrompt.Trim()}\n\n{Preamble}\n\n{json}"`.
- **JSON** via `JsonSerializer` with `WriteIndented = true` (readable for the
  consuming agent) and `DefaultIgnoreCondition = WhenWritingNull` — so null
  scalars (`custom_id`, `status`, `url`, `due_date`, `priority`, `description`,
  and the whole `list` when both id+name are null) are **omitted**, while
  `assignees` / `tags` / `comments` are always present (empty array when none),
  which is informative for the agent. Explicit snake_case property names
  (`custom_id`, `due_date`) — no naming policy.
- **`description`** truncated to `MaxDescriptionLength` chars (append `…` when
  cut); `IsNullOrEmpty` → omitted.
- **`WritePromptFile`** default `directory` = `<Path.GetTempPath()>/clickup-todo`
  (same `clickup-todo` name the app uses elsewhere); filename
  `agent-prompt-<sanitized-task-id>-<guid:N>.txt` (guid → no collisions across
  rapid dispatches). `directory` is injectable purely so tests write to a scratch
  dir.

### Retention

**Leave the file in place** for the launched session to read — deleting it
eagerly would race the new terminal reading it at startup (the launcher feeds it
via `Get-Content -Raw` / `cat` at run time). OS temp-dir cleanup reclaims it.
Proactive cleanup is not S1's job (the launched session owns the file's life);
noted as a possible future tidy-up, not implemented here.

## Tests (`tests/ClickUpTodo.Tests/AgentPromptComposerTests.cs`)

Pure (all run in CI, no API):
- **Layout:** result is exactly `userPrompt`, blank line, `Preamble`, blank line,
  then the JSON object; user prompt is trimmed.
- **`task` subset:** id/name/status/url/priority/list{id,name}/due_date all map
  with correct values (deserialize `BuildJson` and assert).
- `custom_id` omitted when null, present when set.
- `status` / `url` / `priority` / `due_date` omitted when null.
- **Description:** truncated (+`…`) when over `MaxDescriptionLength`; verbatim
  when short; omitted when empty.
- `assignees` / `tags` serialized as arrays (incl. empty array when none).
- **Comments:** full objects (id, author, date, text, resolved); empty list →
  empty `comments` array.
- **Escaping:** a description/comment with quotes + newlines round-trips (valid
  JSON, values preserved) — content safety since this later reaches a shell via
  the file.
- **File writer:** `WritePromptFile` creates the directory, returns an existing
  path whose contents equal `Compose(...)`, and yields unique paths across calls.

## Out of scope (own issues)

- `A` keybinding + detail-view prompt input + status-line display of the launch
  result — **#26** (S3), which consumes this composer.
- Settings (preamble override / claude path / args / cwd) — **#27** (S4).

## Verification

`dotnet build -c Release` (0 warn / 0 err), `dotnet test -c Release`,
`dotnet format`. Fully headless-verifiable — pure logic + temp-file I/O, no TUI,
no ClickUp API.
