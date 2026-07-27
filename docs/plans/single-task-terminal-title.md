# Set the terminal window/tab title in single-task launch mode (#418)

Part of the multi-tab epic (#292). When the app is launched with `--task <id>`
(single-task launch mode, #296), set the host terminal's window/tab title to the
launched task's id + name so a user with several `clickup-todo --task` tabs open
can tell them apart at a glance from the tab strip / window title alone.

## Acceptance criteria (from #418)

- Launching `clickup-todo --task <id>` sets the terminal title to
  `{ID}: {Task Title}`.
- Prefer the human-facing **custom id** when the task has one; otherwise the
  numeric id.
- Truncate the whole `{ID}: {Title}` string to **40 characters** total (tab
  titles are short).
- Set it via the standard terminal title mechanism (an OSC title escape).

## Design

The dashboard (`TodoApp`) shows the whole working set, so there is no single
task to title — this is a **single-task-mode-only** behaviour, so it lives on the
`SingleTaskApp` host, not the shared `TodoApp` path.

**Mechanism (probed).** Terminal.Gui 2.4.10 already drives the host terminal's
window/tab title from the **top-level `Window.Title`**: the window's border view
emits an OSC title escape via the driver's `SetTerminalTitle` whenever that title
changes (deduped against a `_lastTitle`). The dashboard sets the window title to
`AppBranding.WindowTitle(workspace)` (`"ClickUp Simple CLI — <workspace>"`), which
is why a `--task` tab currently shows exactly that in its terminal tab — identical
across every tab, so it can't distinguish them.

So the fix is **not** to write a raw OSC escape ourselves — Terminal.Gui would
clobber it with the window title on the first draw — it is to give
`SingleTaskApp`'s window the **task** as its title. Terminal.Gui then emits it to
the terminal for free, dedups it, and re-emits correctly on resize: no races, no
reflection, no fighting the framework.

### `TerminalTitle` (new, pure) — `src/ClickUpTodo/Tui/TerminalTitle.cs`

`ForTask(id, customId, name, maxLength = 40)` → the title text.

- Id part: `customId` when it is non-blank, else `id`.
- Compose `"{idPart}: {name}"`; when `name` is blank, just `"{idPart}"` (no
  dangling colon).
- **Sanitize** control characters (ESC, BEL, newlines, tabs, other C0/C1) to a
  single space each, so a stray control char in a name can neither corrupt the
  window-frame draw nor break the OSC title escape Terminal.Gui emits from the
  title. Sanitizing before the truncate keeps length predictable (control chars
  collapse 1:1), so the 40-char cut lands where the visible text says.
- Truncate to `maxLength` characters, then trim any trailing whitespace left by
  the cut so the title never ends mid-space.

Kept as a pure function so it is fully unit-testable in CI without a terminal or
a Terminal.Gui host.

### Wire-in — `SingleTaskApp.Build`

Replace the window's title with `TerminalTitle.ForTask(task.Id, task.CustomId,
task.Name)` (was `AppBranding.WindowTitle`). This is the whole change on the host
side. The outer window frame now names the task (≤40 chars) instead of repeating
the product branding — apt in single-task mode, where the entire tab *is* that
one task, and where the inner `TaskDetailScreen` frame still shows the full name.
No new focusable pane, keybinding, list-source, or driver change: the single
sectioned `ListView` input model (#3/#38) is untouched.

## Out of scope / deferred

- **Live title updates** on refresh (a renamed task, a status change) — the
  title is set once at launch from the fetched task. Re-titling the window on
  each `RefreshTask` would keep it current if the task is renamed mid-session,
  but that is rare within a tab's lifetime; left out to keep the change minimal.
  Can follow if wanted.
- **The dashboard (`TodoApp`) title is unchanged** — it keeps
  `AppBranding.WindowTitle` (there is no single task to name).

## Tests

- `TerminalTitleTests` (new, pure, CI): custom-id preference; numeric-id
  fallback; `{id}: {name}` composition; truncation at exactly / over / under 40
  with trailing-space trim; blank name → id only; control-character
  sanitization; non-ASCII names pass through.
- End-to-end: `single_task_title_check.py` (new, under `tests/ClickUpTodo.Tui.E2E/`)
  boots the real `SingleTaskApp` under the PTY in single-task mode and asserts
  pyte's captured window title is the task title, ≤40 chars — the proof that
  Terminal.Gui emits our window Title to the terminal. The wire-in itself is host
  code (not CI-unit-testable per `CLAUDE.md`); this check covers it.
