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
`~/.config/clickup-todo/config.json` elsewhere. The token is stored in your **OS secret store**
wherever one is available:

- **Windows** — encrypted at rest with DPAPI (current-user scope).
- **macOS** — the login **Keychain** (via the built-in `security` tool).
- **Linux** — the **Secret Service** (GNOME Keyring / KWallet) via `secret-tool` (from `libsecret`).

When no secret store is reachable — a headless/SSH box, or a minimal container without `secret-tool`
or a session keyring — the token falls back to an **unencrypted file** (`token.bin`) in the config
directory. On macOS/Linux that file is written with **owner-only permissions (`0600`)** so other
local users can't read it, but it is still **cleartext at rest** — anyone who can read your account's
files (you, or `root`) can read the token, so it's no substitute for the OS store. The first-run setup
states, in plain words, exactly where your token was stored and warns you when the plaintext fallback
was used (with a hint for enabling the secure path). To get the secure path on Linux, install
`libsecret` (which provides `secret-tool`) and run a Secret Service such as `gnome-keyring`. An older
build that wrote a plaintext `token.bin` is migrated into the secret store automatically on the next
launch, and the cleartext file is removed.

Run `clickup-todo --reset` to forget the token and settings and start over.

> **Optional (`config.json`):** set `"feedActivityLookbackDays"` to a positive number to narrow the
> mentions/comments feed to tasks updated in the last _N_ days (a `date_updated_gt` server-side
> window that shrinks the fetch on a busy workspace). `0` (the default) disables it and fetches as
> before. A task with a recent comment stays in the window, since a new comment bumps its update time.

> **Optional (`config.json`) — rebind the launch chords:** the two "open in a new terminal tab"
> (`Ctrl+Enter`) and "open in a split pane" (`Ctrl+Alt+Enter`) gestures are configurable under
> `"launchChords"`, e.g. `{ "launchChords": { "splitPane": "Alt+Enter" } }`. Leaving a chord unset (the
> default) keeps the shipped binding. This is what lets you point the split gesture at `Alt+Enter` after
> unbinding Windows Terminal's `Terminal.ToggleFullscreen` on that key. An unrecognised or conflicting
> value is ignored (the default binding is kept), never a crash. The dashboard task list and the `Ctrl+O`
> quick-open surface honour the rebind — the footer advertises the configured chord. _(Editing these from
> the F10 Settings screen, and honouring the rebind on the Task Detail tabs, are tracked follow-ups.)_

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

The OAuth access token is stored the same way as a personal token (in the OS secret store where one is
available — see [First-run setup](#first-run-setup)), and the active auth mode is recorded in
`config.json` so startup uses the right scheme. To use a different registered
redirect URL, set `CLICKUP_OAUTH_REDIRECT_URI` (it must match the URL registered in your app).

## Keyboard shortcuts

Command actions use modifier chords / function keys so bare letters stay free for the list's
type-ahead search. The **Icon** column is the glyph that labels each action on the app's bottom help
bar, so you can match a footer icon to what it does. Press `F1` in the app for the same list on the
full, per-screen help view (each screen also shows its own contextual shortcuts on the footer).

### Task list

| Key           | Icon | Action                                                        |
| ------------- | :--: | ------------------------------------------------------------- |
| `↑` / `↓`     |      | Move between tasks                                            |
| `Tab`         |      | Jump to the next section (**Current Focus** ↔ **Tasks**)      |
| `Ctrl+U`      |      | Quick Updates — set status / priority / assignees             |
| `Enter`       |      | Open the focused task in **Task Detail**                      |
| `Ctrl+O`      | 🗁   | Open a task by id, custom id, or URL                          |
| `Ctrl+Enter`  |      | Open the focused task in a **new terminal tab** (`Ctrl+`Left-Click a row does the same) |
| `Ctrl+N`      | ➕   | New task                                                      |
| `Ctrl+B`      | 🌐   | Open the focused task in your browser                         |
| `Ctrl+P`      | 📌   | Pin / unpin the focused task to the Focus pane                |
| `Ctrl+E`      | 🔔   | Toggle the mentions & comments feed                          |
| `F1`          | ℹ    | Show help + full shortcut list                                |
| `F10`         | ⚙    | Settings                                                      |
| `F2`          | ✏    | Rename the focused task's title                               |
| `F3`          | ⧩ ▼▲ ⛚ | Filter / sort / group                                      |
| `F4`          |      | Cycle the subtasks view                                       |
| `F5`          | ↻    | Refresh now (`Ctrl+R` is an alias)                           |
| `F6`          |      | Cycle status/priority badges                                  |
| `F12`         | 👁✅  | Toggle completed tasks                                        |
| `→` / `←`     |      | Expand / collapse the focused parent's subtasks              |
| `Ctrl+→` / `Ctrl+←` | | Expand-all / collapse-all subtasks                        |
| `type`        |      | Type-ahead search by task title                               |
| `Ctrl+Q` / `Esc` |   | Quit — confirms first (`Y`/`Enter` exits, `N`/`Esc` stays; F10 to turn off) |

### Task Detail

| Key                   | Icon | Action                                                 |
| --------------------- | :--: | ------------------------------------------------------ |
| `↑` / `↓`, `PgUp` / `PgDn` | | Scroll the pane                                      |
| `Ctrl+←` / `Ctrl+→`   |      | Switch tab (Description / Comments / Other)            |
| `Ctrl+PgUp` / `Ctrl+PgDn` | ▼▲ | Order the comments & activity feed                    |
| `Ctrl+A`              | ✨   | Dispatch a Claude session for this task                |
| `Ctrl+N`              | ➕   | Add a comment                                          |
| `Del`                 | 🗑   | Delete a comment on the Comments / Stream tab — pick one, then confirm |
| `Ctrl+E`              | ✏    | Edit the description                                   |
| `Ctrl+B`              | 🌐   | Open the task in your browser (keeps the view open — see below) |
| `Tab` / `Shift+Tab`   |      | Move the focus highlight to the next / previous link in the pane |
| `Enter`               |      | Follow the focused link (task link → Task Detail, other → browser) |
| Left-click a link     |      | Follow it — see [Follow links in a task's text](#follow-links-in-a-tasks-text) |
| `Ctrl+U`              |      | Quick Updates for this task                            |
| `Ctrl+O`              | 🗁   | Open another task by id, custom id, or URL             |
| `F5`                  | ↻    | Refresh                                                |
| `F6`                  |      | Cycle status/priority badges in the Task Tree tab (opens in the list's badge state) |
| `F1`                  | ℹ    | Help                                                   |
| `Esc`                 |      | Back to the list                                       |

**Quitting asks first.** `Esc` means "go back" on every screen, so the one place it would be
destructive — leaving the app — is guarded: `Esc` (or `Ctrl+Q`) from the main list, or from the launch
task in a single-task tab (`--task`), shows a confirmation. `Y`/`Enter` exits; `N`/`Esc` returns you to
exactly where you were, with your cursor and tab unchanged. `Esc` anywhere else still just goes back.
The guard is on by default; turn it off in Settings (`F10` → *Confirm on exit*) to restore a one-key quit.

**`Ctrl+B` in Task Detail now keeps the task on screen.** Opening a task in the browser never leaves
the app, and by default it no longer closes the detail view either — you stay exactly where you were
(matching the `Ctrl`+click-a-task-link gesture, and making the dashboard and single-task `--task` tab
behave the same). This is a change from earlier builds, where `Ctrl+B` also returned you to the list —
and where, in a `--task` tab, it *quit the program*. For the old close-on-`Ctrl+B` behaviour, set
**Settings (`F10`) → Detail view → `Ctrl+B` → "Open browser + close"**; a `--task` launch task always
stays open regardless, since there is nothing to go back to.

Quick Updates opens with `Ctrl+U` from both the main list and Task Detail. Pinned tasks persist
across restarts. The list refreshes in the background on your configured interval, and your cursor
stays on the same task across refreshes so the screen stays steady.

### Follow links in a task's text

Links in a task's **Description**, **Comments** and **Stream** panes are underlined and clickable:

| Gesture | Task link (`app.clickup.com/t/…`) | Any other link |
| --- | --- | --- |
| Left-click | Opens that task's **Task Detail** here — `Esc` walks back to the one you came from | Opens in your **browser** |
| `Ctrl+`Left-Click | Opens in your **browser** (or a **new terminal tab** — see below) | Opens in your **browser** |
| `Ctrl+Shift+`Left-Click | The **other** of browser / new terminal tab | Opens in your **browser** |
| `Tab` / `Shift+Tab` then `Enter` | Same as left-click — steps a focus highlight across the pane's links and follows the focused one | Opens in your **browser** |

`Tab`/`Shift+Tab` step a focus highlight over the links in the current pane (wrapping at the ends) and
`Enter` follows the focused one — the keyboard equivalent of a left-click, for when you'd rather not reach
for the mouse. `Ctrl+`Left-Click on a **task** link has a configurable destination — **open in your
browser** (the default, Windows Terminal's own "open this link" gesture) or **open the task in a new
terminal tab** — set under **Settings → Detail view → Ctrl+Click task link** (`F10`); `Ctrl+Shift+`Left-Click
does the other one. A **web** link always opens in the browser, whatever the modifiers. A task link works
with either a plain ClickUp id or a **custom id** (`/t/{teamId}/{customId}`). In single-task mode
(`--task`) every link opens in the browser, since that mode has no list to stack another task on top of.

### Open a task in a new terminal tab

`Ctrl+Enter` (or `Ctrl+`Left-Click on a row) opens the focused task in **its own terminal tab**,
running `clickup-todo --task <id>` there — handy for parking a task you're actively working in its
own tab while triaging the rest in the main list. It uses the same cross-platform terminal detection
as agent dispatch: on **Windows** it opens a Windows Terminal tab (or a PowerShell/cmd window);
on **macOS**, an iTerm2 tab or a Terminal.app window; on **Linux**, a tab in gnome-terminal/konsole
(or a window via `$TERMINAL` / a detected emulator, and a `tmux` window when you're inside tmux).
Where a tab can't be targeted it opens a new **window**; and where no terminal can be launched at all
(e.g. a locked-down or headless session), the exact command is **copied to your clipboard** and shown
on the status line so you can run it yourself. Requires `clickup-todo` to be installed on your `PATH`
(the global tool) — a `dotnet run` dev launch can't relaunch itself, so it shows the copy-command
fallback.

A tab launched this way (or any `clickup-todo --task <id>`) **titles its terminal window/tab** with
the task — `{id}: {title}`, using the custom id when the task has one, truncated to 40 characters — so
several single-task tabs stay distinguishable at a glance from the tab strip alone.

#### Which terminals are detected, and a custom launch command

Auto-detection (used by both the task-tab launch and agent dispatch) probes these, in order, and
uses the first one present:

- **Windows** — Windows Terminal → `pwsh` → `powershell` → `cmd` (choose a specific one with the
  **Preferred terminal** setting in F10, or `AgentDispatch.PreferredTerminal` in `config.json`).
- **macOS** — iTerm2 (for a new tab, when you're inside it) then Terminal.app.
- **Linux** — `$TERMINAL`, then `x-terminal-emulator`, `gnome-terminal`, `konsole`,
  `xfce4-terminal`, `alacritty`, `kitty`, `wezterm`, `foot`, `xterm`, `terminator`; a `tmux` window
  when you're inside tmux.

If your emulator isn't in that list, or you want to **prefer a specific one on macOS/Linux** (where
`PreferredTerminal` doesn't apply), set a **custom terminal launch command** — the
"Custom terminal cmd" field in F10, or `"customTerminalCommand"` under `agentDispatch` in
`config.json`. When set and its executable is on your `PATH`, it's tried **first**, ahead of
auto-detection; leave it blank for auto-detection only. It's a shell-style command line where a `{}`
placeholder marks where the launched command is inserted (appended if you omit it):

```jsonc
// ~/.config/clickup-todo/config.json  (under "agentDispatch")
"customTerminalCommand": "ghostty -e {}"     // or: "kitty {}", "wezterm start -- {}",
                                             // "alacritty -e {}", "gnome-terminal --tab -- {}"
```

You control window-vs-tab through your own template (e.g. `gnome-terminal --tab -- {}`). The command
runs the task tab / `claude` session the same way the built-in emulators do, so a wrapper script works
too. If the executable isn't found, it's skipped and auto-detection runs as usual. Only the **first**
`{}` is the splice point (extra `{}` are literal). On **Windows** the payload is a PowerShell command,
so a custom command also needs `pwsh` or `powershell` present (effectively always true — Windows
PowerShell ships in-box); otherwise it's skipped and auto-detection runs.

## Mentions & Comments feed

Press `Ctrl+E` to open a feed of recent comments and `@`-mentions across the tasks assigned to you
(`F3` toggles a mentions-only view). ClickUp has no inbox/mentions API, so the feed is synthesised
from the comments on your **assigned** tasks — which means a mention on a task you aren't assigned to
won't appear unless a small **per-Space ClickUp Automation** turns mentions into assignments.

You can also open the feed in **its own window/tab** with `clickup-todo --feed` — the same view, hosted
standalone so you can keep it beside your work instead of toggling it in the dashboard. The dashboard's
`Ctrl+E` is unchanged; `--feed` is just an additional way in.

A comment that has a reply thread shows a `N replies` count on its feed row; the reply bodies
themselves aren't fetched for the feed (that would fan out across every assigned task), but they're
rendered nested under the comment in the Task Detail **Comments** / **Stream** tabs when you open the
task.

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
