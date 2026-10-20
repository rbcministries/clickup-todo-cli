# Beta tester guide

Thanks for helping test **ClickUp Simple CLI** — a small, keyboard-driven terminal task list
for your ClickUp tasks. This guide gets you from download to triaging tasks in a few minutes.
No developer tools required.

> **Which build is this for?** The beta ships a **Windows** build (`win-x64`). If you're on
> macOS or Linux, native builds aren't published yet — you can still run from source (see
> [CONTRIBUTING.md](../CONTRIBUTING.md)), but the polished tester path below is Windows-only for now.

## 1. Download

1. Go to the repo's **[Releases](https://github.com/rbcministries/clickup-todo-cli/releases)** page.
2. Open the latest **Pre-release** (tagged like `v0.3.0-beta.1`).
3. Under **Assets**, download `clickup-todo-<version>-win-x64.exe`.

That single file is the whole app — nothing to install.

## 2. Run it

Double-click the `.exe`, or run it from a terminal (PowerShell / Windows Terminal) so you can
see it in your usual console:

```powershell
.\clickup-todo-0.3.0-beta.1-win-x64.exe
```

> **"Windows protected your PC" (SmartScreen)?** That's expected — the beta build isn't
> code-signed yet. Click **More info → Run anyway**. (If your org blocks it entirely, let the
> maintainer know.)

## 3. First-run setup

The app walks you through a short setup the first time:

1. **Paste a ClickUp personal API token.** In ClickUp: **Settings → Apps → API Token**; it
   starts with `pk_`. The token is validated immediately. *(It's kept in your OS secret store —
   on Windows that's DPAPI, tied to your Windows user.)*
2. **Choose your workspace.**
3. **Pick your "Personal Tasks" list.**
4. **Pick a refresh interval** (default 60 seconds).

To start over at any time, run it with `--reset`.

## 4. Using it

It shows the tasks **assigned to you** plus your **Personal Tasks** list, and refreshes in the
background. The essentials:

| Key | Action |
| --- | --- |
| `↑` / `↓` | Move between tasks |
| `Tab` | Switch between **Current Focus** and **Tasks** |
| `Enter` | Open the task in **Task Detail** |
| `Ctrl+U` | Quick Updates — status / priority / assignees / lists |
| `Ctrl+N` | New task (or subtask) |
| `F2` | Rename whatever's highlighted |
| `Ctrl+O` | Open any task by id, custom id, or URL |
| `Ctrl+Enter` | Open the task in its own terminal tab |
| `Ctrl+B` | Open the task in your browser |
| `Ctrl+P` | Pin / unpin to the Focus pane |
| `Ctrl+E` | Mentions & Comments feed |
| `F5` | Refresh now · `F1` Help · `F10` Settings · `Ctrl+Q`/`Esc` Quit (asks first) |

Inside **Task Detail**, `Ctrl+←`/`Ctrl+→` switch tabs (Stream · Description · Comments · Other ·
Checklists · Task Tree), `Ctrl+N` adds a comment, `Ctrl+E` edits the description,
`Del` deletes the highlighted comment / checklist item / subtask, and `Ctrl+A` dispatches an
agent session for the task. Links in a task's text are underlined and clickable — or `Tab` to
one and press `Enter`.

Press `F1` in the app for the full, per-screen shortcut list. More details are in the
[README](../README.md).

## Coming from an earlier beta? Five keys moved

| You used to press | Now |
| --- | --- |
| `Space` — change status | **`Ctrl+U`** — Quick Updates (status, priority, assignees, lists) |
| `F2` — Settings | **`F10`** — Settings. `F2` is now **Rename** |
| `Tab` — switch Task Detail tab | **`Ctrl+←` / `Ctrl+→`** |
| `Esc` / `Ctrl+Q` — instant quit | Quitting asks first (turn it off in `F10` → *Confirm on exit*) |
| `Ctrl+B` in Task Detail — also closed the view | Keeps the task on screen (`F10` → *Detail view* to restore) |

Your token, settings and pinned tasks carry over — just replace the `.exe`.

## What's in this beta

**Reading & triage:** the task list (status, pin, group / sort / filter, subtask nesting), Task
Detail, the Mentions & Comments feed, background refresh with local caching.

**Writing:** new tasks and subtasks with custom fields, comments and threaded replies,
description editing, @-mentions of real ClickUp users, checklists (add / rename / resolve /
assign / reorder), renames, and deletes — all with a confirmation where it matters.

**Getting around:** open any task by id or URL (`Ctrl+O`), park a task in its own terminal tab
(`Ctrl+Enter` / `--task`), run several tabs at once and see each other's edits, mouse clicks
alongside every keyboard shortcut.

**Agents:** dispatch a coding-agent session from a task, into a new window, tab, or split pane.

**Not yet:** managing **tags**, and the in-app agent chat surface. Both are planned.

## Known limitations

- **Windows only** for now; no signed/notarized build yet (hence the SmartScreen prompt).
- **Sluggish keys?** It's the terminal driver, not the app. Try `--driver windows` (native
  Win32 input is usually snappiest on Windows). See the README's Troubleshooting section.
- **Mentions feed** only shows mentions on tasks **assigned to you** unless a per-Space ClickUp
  automation turns mentions into assignments — see
  [docs/mention-assignee-automation.md](mention-assignee-automation.md).
- **Opening a task in a new tab** (or split pane) needs `clickup-todo` on your `PATH`. When no
  terminal can be launched, the command is copied to your clipboard instead.
- **Two tabs editing the same field** — the later save wins, silently. ClickUp's API has no
  version check, so there's nothing to warn on.

## Found a bug?

Please [open an issue](https://github.com/rbcministries/clickup-todo-cli/issues/new?template=bug_report.yml)
and include:

- **What happened** vs. what you expected, and steps to reproduce.
- Your **Windows version** and **terminal** (e.g. Windows Terminal, PowerShell, conhost).
- The **driver** shown in the status line at startup (e.g. `ansi` / `windows`).
- The **beta version** (from the release you downloaded).

Small quirks and papercuts are worth reporting too — that's exactly what the beta is for. 🙏
