# Beta tester guide

Thanks for helping test **ClickUp Simple CLI** — a small, keyboard-driven terminal task list
for your ClickUp tasks. This guide gets you from download to triaging tasks in a few minutes.
No developer tools required.

> **Which build is this for?** The beta ships a **Windows** build (`win-x64`). If you're on
> macOS or Linux, native builds aren't published yet — you can still run from source (see
> [CONTRIBUTING.md](../CONTRIBUTING.md)), but the polished tester path below is Windows-only for now.

## 1. Download

1. Go to the repo's **[Releases](https://github.com/rbcministries/clickup-todo-cli/releases)** page.
2. Open the latest **Pre-release** (tagged like `v0.1.0-beta.1`).
3. Under **Assets**, download `clickup-todo-<version>-win-x64.exe`.

That single file is the whole app — nothing to install.

## 2. Run it

Double-click the `.exe`, or run it from a terminal (PowerShell / Windows Terminal) so you can
see it in your usual console:

```powershell
.\clickup-todo-0.1.0-beta.1-win-x64.exe
```

> **"Windows protected your PC" (SmartScreen)?** That's expected — the beta build isn't
> code-signed yet. Click **More info → Run anyway**. (If your org blocks it entirely, let the
> maintainer know.)

## 3. First-run setup

The app walks you through a short setup the first time:

1. **Paste a ClickUp personal API token.** In ClickUp: **Settings → Apps → API Token**; it
   starts with `pk_`. The token is validated immediately. *(On Windows it's stored encrypted at
   rest, tied to your Windows user.)*
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
| `Ctrl+U` | Quick Updates — status / priority / assignees |
| `Enter` | Open the task in **Task Detail** |
| `Ctrl+B` | Open the task in your browser |
| `Ctrl+P` | Pin / unpin to the Focus pane |
| `Ctrl+E` | Mentions & Comments feed |
| `F5` | Refresh now · `F1` Help · `Ctrl+Q`/`Esc` Quit |

Press `F1` in the app for the full, per-screen shortcut list. More details are in the
[README](../README.md).

## What's in this beta

**Working:** the task list & triage (status, pin, group/sort/filter), the Task Detail view,
the Mentions & Comments feed, background refresh with local caching, and launching an agent
session from a task (Dispatch).

**Not in this beta yet:** creating new tasks and the inline Quick Updates panel (status /
priority / assignees from the list) are still in progress and land in a later beta.

## Known limitations

- **Windows only** for now; no signed/notarized build yet (hence the SmartScreen prompt).
- **Sluggish keys?** It's the terminal driver, not the app. Try `--driver windows` (native
  Win32 input is usually snappiest on Windows). See the README's Troubleshooting section.
- **Mentions feed** only shows mentions on tasks **assigned to you** unless a per-Space ClickUp
  automation turns mentions into assignments — see
  [docs/mention-assignee-automation.md](mention-assignee-automation.md).

## Found a bug?

Please [open an issue](https://github.com/rbcministries/clickup-todo-cli/issues/new) and include:

- **What happened** vs. what you expected, and steps to reproduce.
- Your **Windows version** and **terminal** (e.g. Windows Terminal, PowerShell, conhost).
- The **driver** shown in the status line at startup (e.g. `ansi` / `windows`).
- The **beta version** (from the release you downloaded).

Small quirks and papercuts are worth reporting too — that's exactly what the beta is for. 🙏
