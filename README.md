# ClickUp Simple CLI

A lightweight, keyboard-driven terminal task list for [ClickUp](https://clickup.com) — built in
.NET so it's easy to run and maintain, without loading the full memory-hungry web app just to triage
your tasks.

It shows the tasks **assigned to you** and the tasks on your **Personal Tasks list**, refreshes from
the ClickUp REST API on a configurable interval, and lets you change a task's status and pin tasks to
a "Current Focus" pane — all from the keyboard.

<img width="770" height="219" alt="Screenshot 2026-07-01 152130" src="https://github.com/user-attachments/assets/eab69023-d41e-457e-9aa9-061ce2f8b360" />

_Pin focused tasks, group by list/status/priority, and more_

<img width="1144" height="127" alt="Screenshot 2026-07-01 152244" src="https://github.com/user-attachments/assets/38d750a7-eddf-48b8-952a-ff4fbf7810dc" />

_Keyboard shortcuts for common operations_

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
`~/.config/clickup-todo/config.json` elsewhere. On **Windows** the token is stored
**encrypted at rest** using DPAPI (current-user scope). On **other platforms** it currently
falls back to an **unencrypted file** on disk — strengthening this with the OS secret store
(macOS Keychain / Linux Secret Service) is tracked in
[#306](https://github.com/rbcministries/clickup-todo-cli/issues/306) and is a prerequisite for
publishing pre-built macOS/Linux binaries. Running from source is unaffected.

Run `clickup-todo --reset` to forget the token and settings and start over.

> **Optional (`config.json`):** set `"feedActivityLookbackDays"` to a positive number to narrow the
> mentions/comments feed to tasks updated in the last _N_ days (a `date_updated_gt` server-side
> window that shrinks the fetch on a busy workspace). `0` (the default) disables it and fetches as
> before. A task with a recent comment stays in the window, since a new comment bumps its update time.

> **Why a personal token by default?** ClickUp's OAuth flow requires a client **secret**, which
> can't be safely shipped in a public repo (there's no PKCE/public-client flow). A personal token is
> equally capable for your own tasks and keeps nothing secret in the repo, so it's the default path.
> If you'd rather sign in with OAuth, you can — using **your own** ClickUp app (below).

### Optional: sign in with OAuth (bring your own app)

OAuth is an **opt-in alternative** to the personal token — the personal-token path stays the default
and is unaffected. To enable it you register your own ClickUp OAuth app so no secret ever lives in
this repo:

1. In ClickUp, go to **Settings → Apps → API → Create an App** (or ClickUp's OAuth app settings) and
   register an app. Set its **Redirect URL** to `http://localhost:53682/callback`.
2. Provide the app's credentials to the CLI, either via environment variables:

   ```bash
   export CLICKUP_OAUTH_CLIENT_ID=...       # your app's client id
   export CLICKUP_OAUTH_CLIENT_SECRET=...   # your app's client secret
   ```

   or via a **gitignored** `oauth-app.json` in the config directory
   (`~/.config/clickup-todo/` or `%APPDATA%\clickup-todo\`):

   ```json
   { "clientId": "...", "clientSecret": "..." }
   ```

3. Run the app (or `clickup-todo --reset`). When credentials are present, setup asks whether to use a
   personal token (default) or **Sign in with ClickUp (OAuth)**. Choosing OAuth opens your browser to
   authorize; the CLI captures the redirect on `localhost` and exchanges it for an access token. If
   the local listener can't bind (locked-down environment), it falls back to letting you **paste the
   `code`** from the browser's address bar.

The OAuth access token is stored the same way as a personal token (encrypted at rest), and the active
auth mode is recorded in `config.json` so startup uses the right scheme. To use a different registered
redirect URL, set `CLICKUP_OAUTH_REDIRECT_URI` (it must match the URL registered in your app).

## Keyboard shortcuts

These are the main task-list shortcuts. Command actions use modifier chords / function keys so bare
letters stay free for the list's type-ahead search. Press `F1` in the app for the full, per-screen
list (each screen shows its own contextual shortcuts on the footer).

| Key           | Action                                                        |
| ------------- | ------------------------------------------------------------- |
| `↑` / `↓`     | Move between tasks                                            |
| `Tab`         | Jump to the next section (**Current Focus** ↔ **Tasks**)      |
| `Ctrl+U`      | Quick Updates — set status / priority / assignees             |
| `Enter`       | Open the focused task in **Task Detail**                      |
| `Ctrl+B`      | Open the focused task in your browser                         |
| `Ctrl+P`      | Pin / unpin the focused task to the Focus pane                |
| `Ctrl+N`      | New task                                                      |
| `Ctrl+E`      | Toggle the mentions & comments feed                          |
| `F1`          | Show help + full shortcut list                                |
| `F2`          | Settings                                                      |
| `F3`          | Filter / sort / group                                         |
| `F4`          | Cycle the subtasks view                                       |
| `F5`          | Refresh now (`Ctrl+R` is an alias)                           |
| `F6`          | Cycle status/priority badges                                  |
| `F12`         | Toggle completed tasks                                        |
| `→` / `←`     | Expand / collapse the focused parent's subtasks              |
| `Ctrl+→` / `Ctrl+←` | Expand-all / collapse-all subtasks                     |
| `type`        | Type-ahead search by task title                               |
| `Ctrl+Q` / `Esc` | Quit                                                      |

Quick Updates opens with `Ctrl+U` from both the main list and Task Detail. Pinned tasks persist
across restarts. The list refreshes in the background on your configured interval, and your cursor
stays on the same task across refreshes so the screen stays steady.

## Mentions & Comments feed

Press `Ctrl+E` to open a feed of recent comments and `@`-mentions across the tasks assigned to you
(`F3` toggles a mentions-only view). ClickUp has no inbox/mentions API, so the feed is synthesised
from the comments on your **assigned** tasks — which means a mention on a task you aren't assigned to
won't appear unless a small **per-Space ClickUp Automation** turns mentions into assignments.

`F6` toggles a **recent-activity** source: your recently-updated assigned tasks (by ClickUp
`date_updated`), merged into the feed newest-first so "what changed on my tasks" sits alongside the
comments. It's off by default and, because ClickUp has no task-activity-history API, approximates
activity via the last-updated time only.

That automation is an optional user prerequisite the app can't set up or verify for you. See
[docs/mention-assignee-automation.md](docs/mention-assignee-automation.md) for the exact
trigger/condition/action, setup steps, and the caveats (per-Space, paid, not retroactive, and its
blast radius on your ClickUp "Assigned to me").

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

**Slow arrow-key navigation on remote/slow terminals.** With the `ansi` driver the app diffs
frames and only sends the terminal the rows that actually changed (~0.9 KB per keypress instead
of ~18 KB for the whole visible list), which keeps navigation snappy over SSH and on slow terminal
emulators. Changed rows are re-sent whole, byte-identical to the stock renderer, so wide/emoji
text renders the same either way. The status line shows `diffed output` at startup when this is
active. If a terminal ever renders artifacts with it, set `CLICKUP_TODO_NO_DIFF=1` to fall back
to the stock full-repaint output.

## Other great CLI tools

This project is a focused, always-on triage **dashboard**. If you need a broader,
command-driven ClickUp CLI (scripting, full CRUD, agent automation), these are well worth a look:

| Tool | What it is | Notable features | Language |
| --- | --- | --- | --- |
| [krodak/clickup-cli](https://github.com/krodak/clickup-cli) (`cup`) | Full-featured CLI for humans **and** AI agents | Near-complete API coverage (tasks, subtasks, dependencies, comments, chat, time tracking, sprints, custom fields, webhooks); interactive pickers in a TTY + clean JSON/Markdown when piped; multiple profiles; ships a Claude Code skill (`cup skill`) | TypeScript |
| [triptechtravel/clickup-cli](https://github.com/triptechtravel/clickup-cli) | Developer-workflow CLI with deep Git integration | Auto-detects task IDs from branch names and links PRs/branches/commits; sprint dashboard; time tracking and timesheets; Docs support; comments with @mentions; server-side search; JSON output | Go |
| [sensor-industries/clickup-cli](https://github.com/sensor-industries/clickup-cli) | Lightweight task-authoring CLI | Create/update/delete tasks; comments; checklists and items; set priority, estimates, assignees, sprint points, and custom fields; markdown descriptions from a file | JavaScript |
| [fantasticrabbit/ClickupCLI](https://github.com/fantasticrabbit/ClickupCLI) | General-purpose API CLI with OAuth2 sign-in | Browser-based OAuth2 auth flow (or manual token); get task (with subtasks and custom IDs); get list; local team/port config | Go |

## License

[MIT](LICENSE) © RBC Ministries
