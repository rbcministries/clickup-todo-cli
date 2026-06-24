# clickup-todo

A lightweight, keyboard-driven terminal to-do list for [ClickUp](https://clickup.com) — built in
.NET so it's easy to run and maintain, without loading the full memory-hungry web app just to triage
your tasks.

It shows the tasks **assigned to you** and the tasks on your **Personal Tasks list**, refreshes from
the ClickUp REST API on a configurable interval, and lets you change a task's status and pin tasks to
a "Current Focus" pane — all from the keyboard.

```
┌ ClickUp To-Do — Acme Workspace ─────────────────────────────────────────────┐
│┌ ★ Current Focus (1) ───────────────────────────────────────────────────────┐│
││ [in progress] Ship the Q3 report  · Personal Tasks  · due Jul 1             ││
│└──────────────────────────────────────────────────────────────────────────-─┘│
│┌ To-Do (12) ──────────────────────────────────────────────────────────────-─┐│
││ [to do] Review onboarding PR  · Engineering                                 ││
││ [to do] Reply to vendor email  · Personal Tasks  · due Jun 28               ││
││ …                                                                           ││
│└──────────────────────────────────────────────────────────────────────────-─┘│
│ Updated 14:02:31 · 13 task(s) · refresh every 60s                            │
│ ↑/↓ move · Tab switch pane · Space set status · P pin · Enter open · Q quit  │
└──────────────────────────────────────────────────────────────────────────-──┘
```

## Install

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) (or runtime) to be installed.

As a .NET global tool, from a local package:

```bash
dotnet pack src/ClickUpTodo/ClickUpTodo.csproj -c Release
dotnet tool install --global --add-source ./src/ClickUpTodo/bin/Release ClickUpTodo.Cli
```

Then run:

```bash
clickup-todo
```

Or just run from source while developing:

```bash
dotnet run --project src/ClickUpTodo
```

## First-run setup

On first launch the app walks you through a short setup:

1. **Paste a ClickUp personal API token.** Generate one in ClickUp under
   **Settings → Apps → API Token** (it starts with `pk_`). The token is validated immediately.
2. **Choose your workspace.**
3. **Choose your "Personal Tasks" list** from your workspace's spaces/folders/lists.
4. **Pick a refresh interval** (default 60 seconds).

Settings are saved to `%APPDATA%\clickup-todo\config.json` (on Windows) or
`~/.config/clickup-todo/config.json` elsewhere. The token is stored **encrypted at rest** using
Windows DPAPI (current-user scope); on other platforms it falls back to a base64-obfuscated file.

Run `clickup-todo --reset` to forget the token and settings and start over.

> **Why a personal token and not OAuth sign-in?** ClickUp's OAuth flow requires a client **secret**,
> which can't be safely shipped in a public repo (there's no PKCE/public-client flow). A personal
> token is equally capable for your own tasks and keeps nothing secret in the repo. OAuth with
> user-supplied app credentials is tracked as a follow-up issue.

## Keyboard shortcuts

| Key       | Action                                            |
| --------- | ------------------------------------------------- |
| `↑` / `↓` | Move between tasks                                |
| `Tab`     | Switch between the **Current Focus** and **To-Do** panes |
| `Space`   | Set the focused task's status (from its list's statuses) |
| `P`       | Pin / unpin the focused task to the Focus pane    |
| `Enter`   | Open the focused task in your browser             |
| `R`       | Refresh now                                       |
| `?`       | Show help                                         |
| `Q` / `Esc` | Quit                                            |

Pinned tasks persist across restarts. The list refreshes in the background on your configured
interval, and your cursor stays on the same task across refreshes so the screen stays steady.

## How it's built

- **TUI:** [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) v2.
- **ClickUp client:** generated from an OpenAPI spec with [Microsoft Kiota](https://learn.microsoft.com/openapi/kiota/)
  — see [`src/ClickUpTodo/ClickUp/`](src/ClickUpTodo/ClickUp/). The generated code lives in
  `ClickUp/Generated/` and is **not hand-edited**; a thin `ClickUpClient` facade wraps it with
  paging, auth, and mapping to stable domain types.
- **Auth:** ClickUp personal tokens are sent as a raw `Authorization` header (no `Bearer` prefix),
  handled by a custom Kiota `IAuthenticationProvider`.

### Regenerating the API client

The client is generated from a **curated** OpenAPI spec
([`src/ClickUpTodo/ClickUp/clickup-openapi.json`](src/ClickUpTodo/ClickUp/clickup-openapi.json)) — a
corrected subset of ClickUp's official v2 reference. (The official spec's inline, partly-malformed
schemas generate broken C#; the curated spec re-expresses the same endpoints with shared component
schemas. See the file's `description` for details.)

To regenerate after changing the spec:

```bash
dotnet tool restore       # installs the pinned Kiota version
pwsh scripts/regen-client.ps1
```

## Tests

```bash
dotnet test
```

Unit tests (config/token storage) always run. Integration tests hit the real ClickUp API and are
**skipped automatically** unless you provide credentials via environment variables:

- `CLICKUP_TOKEN` — your personal token (enables the basic API tests)
- `CLICKUP_WORKSPACE_ID`, `CLICKUP_LIST_ID` — optional, enable the task/status tests

## Troubleshooting

**Sluggish key response (arrows/Tab feel delayed).** This comes from Terminal.Gui's console
driver, not the app. The default driver is `ansi` (pure ANSI escape sequences); on Windows the
native `windows` driver usually has snappier input:

```bash
clickup-todo --driver windows   # native Win32 input — try this first on Windows
clickup-todo --driver dotnet    # System.Console cross-platform driver
clickup-todo --driver ansi      # pure ANSI driver (default)
```

You can also set `CLICKUP_TODO_DRIVER` (e.g. `CLICKUP_TODO_DRIVER=windows`). The active driver is
shown in the status line at startup. See [issue #3](https://github.com/rbcministries/clickup-todo-cli/issues/3).

## License

[MIT](LICENSE) © RBC Ministries
