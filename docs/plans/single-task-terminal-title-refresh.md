# Update the terminal window/tab title on refresh in single-task mode (#425)

Follow-up to #418 (PR #424), part of the multi-tab epic (#292). #418 titles the host
terminal window/tab in single-task launch mode (`--task <id>`) with the launched task —
`{id}: {name}` (custom id preferred, ≤40 chars) — by setting `SingleTaskApp`'s top-level
`Window.Title` **once, at build time**, from the initially-fetched task. Terminal.Gui
propagates that window title to the terminal via the driver's `SetTerminalTitle`.

## Problem

`SingleTaskApp.RefreshTab` re-fetches the task on F5 / Ctrl+R and on the 30s auto-refresh
tick, feeding fresh data into the detail view. If the launch task is **renamed** (or gains a
custom id) in ClickUp mid-session, the in-app detail updates but the **terminal title stays
stale** — set from the launch-time fetch and never revisited.

## Scope

- In `RefreshTab`'s `Application.Invoke` reconcile block (where `tab.Task`/`tab.Comments` are
  replaced and `tab.Screen.UpdateData` runs), recompute the title from the refreshed detail and
  reassign `_window.Title` **only when it changed**.
- **Only the launch (root) task titles the window** (#418: "the whole tab *is* that one
  task"). Since the #374 Task Tree refactor, walking a tree row stacks a **child** detail tab
  over the root, and `RefreshTab` runs for whichever tab is front-most. Retitling from a child
  would make the terminal tab title jump to whatever the user drilled into — a new behaviour
  #418 never had, and contrary to "identify this `--task` tab by what was launched". So the
  retitle is gated on `ReferenceEquals(tab, _root)`: a stacked child's refresh leaves the
  window title on the launched task. (Only the front-most tab refreshes at all, so in practice
  the root retitles exactly when it is front-most — no child ever competes for the window
  title.)

## Design

### `TerminalTitle.Retitle` (new, pure) — the retitle decision

`Retitle(currentTitle, id, customId, name, maxLength = MaxLength)` → the refreshed title, or
`null` when it is unchanged from `currentTitle`:

```
Retitle(current, id, customId, name) =
    ForTask(id, customId, name) is var next
        ? (next == current ? null : next)   // ordinal compare
        : …
```

- Reuses the existing, already-tested `ForTask` formatter (custom-id preference, blank-name /
  control-char / surrogate-pair / truncation handling) — this issue adds **no** new formatting.
- Returns `null` on no-change so the host skips the assignment entirely. Terminal.Gui already
  dedups on its own `_lastTitle`, so this is belt-and-braces against churn; but it also keeps
  the pure decision unit-testable without a terminal (the `Window.Title` assignment itself is
  host code per `CLAUDE.md`).
- **Ordinal** comparison: a title differing only in case is a real rename worth pushing.

### Wire-in — `SingleTaskApp.RefreshTab`

```csharp
Application.Invoke(() =>
{
    tab.Task = detail;
    tab.Comments = comments;
    tab.Screen.UpdateData(detail, comments);
    // #425: keep the terminal tab title current if the *launch* task was renamed / gained a
    // custom id mid-session. Only the launch (root) task titles the window (#418); a stacked
    // child's refresh (Task Tree #374) leaves it, so the tab stays identifiable by what was
    // launched.
    if (ReferenceEquals(tab, _root)
        && TerminalTitle.Retitle(_window.Title, detail.Id, detail.CustomId, detail.Name) is { } title)
    {
        _window.Title = title;
    }
});
```

No new focusable pane, keybinding, list-source, or driver change: the single sectioned
`ListView` input model (#3/#38) is untouched — this is a one-field reassignment on the reconcile
path.

## Tests

- **Unit (CI)** — `TerminalTitleTests`: `Retitle` returns `null` on an unchanged title; returns
  the new title on a name change and on a newly-assigned custom id; compares ordinally (a
  case-only change re-titles); delegates truncation to `ForTask` (a long renamed name is cut to
  ≤40); a `null` current title yields the composed title. The `ForTask` formatting suite is
  unchanged.
- **End-to-end (`tui-validate`, after `dotnet test` green)** — new
  `single_task_title_refresh_check.py`: boots `SingleTaskApp` in single-task mode with a new
  `E2E_TITLE_REFRESH=1` fake-backend gate that renames the launch task after the boot fetch;
  asserts pyte's `screen.title` leads with the original name at boot, then **changes** to the
  short renamed title after a Ctrl+R refresh — the proof the reassignment reaches the terminal.
  The wire-in is host code (not CI-unit-testable per `CLAUDE.md`); this check covers it. The
  existing `single_task_title_check.py` (#418, no gate) stays green — the rename is opt-in.

## Hard rules honored

- No `Generated/` edits, no spec change / no regen — pure formatter + a host-code reassignment.
- No generated type escapes any boundary; `TerminalTitle` is a plain string formatter.
- Personal-token raw `Authorization` header untouched; no new credentialed test.
- Single sectioned `ListView` model / no second focusable pane preserved.

## Out of scope

- **Dashboard (`TodoApp`) title** — unchanged; it keeps `AppBranding.WindowTitle` (there is no
  single task to name).
- **Retitling the window to a stacked child task** — deliberately not done (see Scope); the
  window title tracks the launched task only.
