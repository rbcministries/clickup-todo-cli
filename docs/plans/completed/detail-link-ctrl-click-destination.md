# Task Detail (F): configurable Ctrl+Click destination for task links (#320)

Issue: [#320](https://github.com/rbcministries/clickup-todo-cli/issues/320) ·
Epic: [#313](https://github.com/rbcministries/clickup-todo-cli/issues/313)

Builds on the already-merged **D** (mouse click activation, #318), **E** (keyboard link
traversal, #319), and **#296** (`--task` single-task launch mode). All three are on `main`.

## Goal

Make the `Ctrl`+click gesture on a **task** link in a detail pane configurable — either **open
in the browser** (today's fixed behaviour) or **open that task in a new terminal tab** — with
`Ctrl+Shift`+click performing the *other* action. Web links are unaffected: they always open in
the browser, whatever the modifiers.

| Gesture (task link) | `Browser` default | `NewTerminalTab` default |
| --- | --- | --- |
| plain click | Task Detail in-app (#318) | Task Detail in-app (#318) |
| `Ctrl`+click | **browser** | **new terminal tab** |
| `Ctrl+Shift`+click | **new terminal tab** | **browser** |

The default is **`Browser`**, so an unconfigured install behaves exactly as #318 shipped.

## Verified current state (repo, at `67e7729`)

- **The dispatcher is the pure `LinkActivator.Resolve(LinkSpan span, bool ctrl)`**
  (`Tui/LinkActivation.cs:45`): `!ctrl && Kind == Task ? OpenTaskDetail : OpenInBrowser`. Its doc
  comment already reserves this issue as *"the plain-click arm with a configurable task
  destination (browser ↔ new terminal tab) and a Shift inversion; this is that arm's one
  caller."* `LinkAction` is `{ OpenInBrowser, OpenTaskDetail }` (`:4`).
- **Mouse seam** — `DetailPaneView.OnMouseEvent` (`Tui/DetailPaneView.cs:553`) admits only a
  plain or `Ctrl`+left-click and **refuses any `Shift`/`Alt`** (`:560-564`); it reads
  `ctrl = Flags.HasFlag(MouseFlags.Ctrl)` (`:572`) and raises
  `LinkActivationRequested(span, Resolve(span, ctrl))` (`:577`). The doc at `:531-534` pre-plans
  admitting `Ctrl+Shift` here ("it joins the resolved-by-synthesized-click arm, since it carries
  `Ctrl`"). `MouseFlags.Shift` is the flag (already checked at `:561`).
- **Keyboard seam** — `ActivateFocusedLink()` (`:282`) raises `Resolve(span, ctrl: false)` on
  `Enter`; #319 deferred a keyboard modifier matrix to this issue but noted `Shift+Enter` is
  unreliable across terminals. **Out of scope here** (see Non-goals).
- **Link model** — `LinkSpan(int Start, int Length, LinkKind Kind, string Url, string? TaskId,
  bool IsCustomTaskId)` (`Tui/TaskLinkExtractor.cs:28`); a task link carries its `TaskId`.
- **New-tab launch** — `TodoApp.LaunchAppTabForTask(string taskId, string name)`
  (`Tui/TodoApp.cs:709`) is the shared launch core (used by the list's `Ctrl+Enter` #301 and
  detail's `Ctrl+Enter` #384): `AppLaunchCommand.ForTask(taskId)` → a `NewTab`
  `TerminalLauncherOptions` → off-thread `_tabLauncher.LaunchAppAsync`, with the clipboard
  fallback when no terminal can be launched. The launcher is the injectable `ITerminalLauncher`.
- **Host routing** — `TodoApp.ActivateLink` (`:1916`) switches on `request.Action`:
  `OpenTaskDetail → ResolveAndOpen(request.Url)`, default → `LaunchBrowser`.
  `SingleTaskApp.ActivateLink` (`:618`) resolves `OpenTaskDetail` in-app (#374) and sends
  **everything else to the browser** via its `default` fall-through — so a new-tab action degrades
  to the browser there with **no change** (single-task new-tab is tracked by #435).
- **Settings** — `DetailViewSettings` (`Configuration/DetailViewSettings.cs`) holds the per-detail
  display prefs (`DefaultTab`/`StreamSort`/`AutoScroll`), all with inline defaults and
  string-serialized enums via `StateJson` (backward-compatible: an absent key → all-defaults).
  Both hosts pass `settings: _config.DetailView` into `TaskDetailScreen`
  (`TodoApp.cs:2006`, `SingleTaskApp.cs:243`); `TaskDetailScreen` reads `prefs.StreamSort` /
  `prefs.AutoScroll` at construction (`Screens/TaskDetailScreen.cs:352-353`). F2 edits the group
  in `Screens/SettingsScreen.cs` (detail section `:145-169`, `Add` list `:308`,
  `SettingsResult` `:17`) and the host writes `_config.DetailView = result.DetailView`
  (`TodoApp.cs:1217`).
- **Enum pattern** — `Configuration/StreamSort.cs` (enum + `*Extensions.Next()` cycle helper) is
  the shape to mirror for a two-value setting.

## Design

Thread a configured destination through the existing dispatcher; add one new `LinkAction`; widen
the pane's modifier guard to admit `Ctrl+Shift`; route the new action to the existing launch core.
No new focusable pane, no new event — the #3 single-`ListView` input model and the #318 payload
are untouched.

### 1. Config (pure) — `Configuration/`

- New enum `TaskLinkCtrlClickDestination { Browser, NewTerminalTab }` (+ `Next()` extension),
  mirroring `StreamSort.cs`. Lives in `Configuration` so both persistence and the Tui dispatcher
  share it without Configuration depending on Tui.
- `DetailViewSettings.TaskLinkCtrlClick { get; set; } = TaskLinkCtrlClickDestination.Browser;`
  Default `Browser` ⇒ byte-identical to #318 for an unconfigured install; absent key → default
  (backward-compatible, no migration).

### 2. Dispatcher (pure) — `Tui/LinkActivation.cs`

- `LinkAction` gains `OpenTaskInNewTab`.
- `Resolve` signature becomes
  `Resolve(LinkSpan span, bool ctrl, bool shift = false, TaskLinkCtrlClickDestination ctrlDestination = TaskLinkCtrlClickDestination.Browser)`.
  The default params keep the existing 2-arg callers (`ActivateFocusedLink`, tests) behaving
  exactly as before. Logic:
  - web link → `OpenInBrowser` (any modifiers);
  - task link, `!ctrl` → `OpenTaskDetail` (plain; `shift` ignored — a plain `Shift`+click is not
    an activation gesture and the pane refuses it);
  - task link, `ctrl` → resolve the effective destination (`shift` inverts `ctrlDestination`),
    then `Browser → OpenInBrowser`, `NewTerminalTab → OpenTaskInNewTab`.

### 3. Pane mouse seam — `Tui/DetailPaneView.cs`

- A public `TaskLinkCtrlClickDestination TaskLinkCtrlClickDestination { get; set; } =
  TaskLinkCtrlClickDestination.Browser;` the screen sets from prefs.
- `OnMouseEvent`: refuse `Shift` **only when `Ctrl` is not also held** (admit `Ctrl+Shift`; keep
  refusing bare `Shift` and any `Alt`). Read `shift = Flags.HasFlag(MouseFlags.Shift)`; a
  `Ctrl+Shift` click carries `Ctrl`, so it already takes the synthesized-plain-click position
  path (`resolvePosition: ctrl`). Raise
  `Resolve(span, ctrl, shift, TaskLinkCtrlClickDestination)`.
- Keyboard `ActivateFocusedLink` is unchanged (plain `Enter` → `Resolve(span, ctrl: false)`).

### 4. Screen — `Tui/Screens/TaskDetailScreen.cs`

- Set `pane.TaskLinkCtrlClickDestination = prefs.TaskLinkCtrlClick` on each pane in the existing
  `foreach` that subscribes `LinkActivationRequested` (`:395`). No host detection: single-task
  mode's host sends the new-tab action to the browser anyway (its `ActivateLink` `default` arm),
  which is the intended single-task fallback (#435).

### 5. Hosts

- `TodoApp.ActivateLink` (`:1916`): add
  `case LinkAction.OpenTaskInNewTab: LaunchAppTabForTask(request.Span.TaskId!, request.Span.TaskId ?? Ellipsize(request.Url));` —
  reusing the existing launch core (re-entrancy guard, off-thread launch, clipboard fallback).
  `request.Span.TaskId` is non-null for a task link (the only kind that resolves to this action).
- `SingleTaskApp` unchanged — `OpenTaskInNewTab` falls to its `default` browser arm (#435 tracks
  a real tab there).

### 6. F2 Settings — `Tui/Screens/SettingsScreen.cs`

- A `taskLinkButton` cycle button in the Detail-view section at `Y=13`, mirroring
  `autoScrollButton`; shift `generalHeader`/`confirmOnExitButton` down to `Y=14`/`Y=15`. Add a
  `TaskLinkCtrlClickText(...)` label helper. Populate `DetailViewSettings.TaskLinkCtrlClick` in the
  `SettingsResult` `new DetailViewSettings { … }` (`:276`). Add the button to the `Add` list.

### 7. Docs

- `README.md` "Follow links in a task's text" table: note that `Ctrl`+click's task-link
  destination is configurable (F2) and `Ctrl+Shift`+click inverts it.

## Known limitation

`LaunchAppTabForTask` runs `clickup-todo --task <TaskId>`. On `main`, `--task` accepts a **plain
task id** (#296); a **custom-id** task link (`/t/{team}/{customId}`, `IsCustomTaskId == true`)
would spawn `--task {customId}`, which only resolves once `--task` custom-id/URL support lands
(#464, PR #470). Plain-id task links — the common `/t/{id}` form and every in-app-generated link —
work today, and custom-id links improve automatically when #464 merges. This is a pre-existing
`--task` boundary, not new to this change; the browser and in-app arms handle custom ids already.

## Phases

1. **Config + dispatcher + unit tests** — the `TaskLinkCtrlClickDestination` enum + `Next()`,
   `DetailViewSettings.TaskLinkCtrlClick`, `LinkAction.OpenTaskInNewTab`, the extended
   `Resolve`. Tests: `LinkActivationTests` (the full modifier × destination × kind matrix) and
   `DetailViewSettingsTests` (default, `.Next()`, round-trip, persisted-as-string,
   backward-compatible load). `dotnet test` green.
2. **Pane + screen + host + settings + pane tests** — admit `Ctrl+Shift` in `OnMouseEvent` and
   pass the destination; set the pane destination from prefs; route `OpenTaskInNewTab` in
   `TodoApp.ActivateLink`; the F2 cycle button; README. Driver-free `DetailPaneViewTests`:
   `Ctrl+Shift`+click on a task link raises `OpenTaskInNewTab` under `Browser` default and
   `OpenInBrowser` under `NewTerminalTab`; `Ctrl`+click follows the default; a bare `Shift`+click
   still falls through; a web link stays browser under every modifier. `dotnet test` green.
3. **E2E + finalize** — after `dotnet test` is green, `tui-validate` a
   `link_ctrl_click_dest_check.py` (mirror of `link_click_check.py`) driving `Ctrl`+ and
   `Ctrl+Shift`+ SGR clicks under both settings, asserting the browser log vs. a recorded tab
   launch; existing link/detail checks still green (tab indices unchanged — this adds no tab).
   Open the draft PR at phase 1, keep it updated, review subagent, mark ready.

## Test plan

- **Unit** — `LinkActivationTests` (dispatcher matrix), `DetailViewSettingsTests` (persistence +
  cycle), `DetailPaneViewTests` (the `Ctrl+Shift` mouse arm, driver-free via `NewMouseEvent`).
- **E2E (`tui-validate`, after `dotnet test`)** — `link_ctrl_click_dest_check.py` for the two
  modifier gestures under both settings; regression on `link_click_check.py`,
  `link_tab_check.py`, `detail_check.py`.
- **No ClickUp boundary** is touched, so no new integration/`SkippableFact` test.
- **Manual (TUI glue, not CI-testable per CLAUDE.md)** — F2 → cycle the new button → Save →
  reopen a task with a task link in its description → `Ctrl`+click and `Ctrl+Shift`+click, once
  per setting, confirming browser vs. new-tab; a web link ignores the setting.

## Non-goals / deferred

- **Keyboard modifier matrix** (a keyboard way to force browser / new-tab on a focused task
  link). #319 left this here, but its own acceptance and this issue's are click-only, and
  `Shift+Enter` is unreliable across terminals; plain `Enter` keeps its #319 behaviour. Not
  shipped; can be revisited if a reliable chord is chosen.
- **New terminal tab in single-task launch mode** — the configured new-tab action degrades to the
  browser in `SingleTaskApp` (consistent with #318/#374); a real tab there is **#435**.
- **Custom-id task links in a new tab** — depend on `--task` custom-id support (#464); see Known
  limitation.
