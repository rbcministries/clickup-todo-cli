# Plan — Split-pane (G, #509): break the Feed out into a `--feed` application host

Sub-issue **G** of the Split-pane epic (#502). Explicitly **independent of the pane work** — it ships
standalone and merely *benefits* from panes later. A third root host selected by `--feed`, following
the `--task` / `SingleTaskApp` precedent (#296) exactly.

## Why

`NotificationsFeedScreen` today only exists *inside* the dashboard host (`TodoApp`), reached via
`Ctrl+E`, so it is modal to the dashboard — you can't watch the feed while working a task. A feed is a
"beside your work" surface, which is what the pane epic makes possible; a standalone host is the
prerequisite that lets the feed be launched in a window / tab / (later) a pane like any other host.

## Grounding (verified in code)

- `Program.cs` already runs **two** root hosts: the dashboard `TodoApp` (default) and `SingleTaskApp`
  (`--task`, early-return at `Program.cs:142-187`). `--feed` slots into the same shape.
- `SingleTaskApp` is the copy template: `Run(driverName)` installs the diffing ANSI backend, calls
  `Application.Init`, `Build()`, `ArmMarkerPoll()`, `Application.Run(_window)`, and tears down in a
  `finally` (`SingleTaskApp.cs:151-181`). It mounts an already-decoupled screen as its own root, wires
  the root screen's `Closed → RequestExit` (exit-confirmation seam #298/#299), and owns a `ShowScreen`/
  `CloseScreen` stack for Help (F1) and the confirm overlay.
- `NotificationsFeedScreen` is fully decoupled: constructed from already-fetched data
  (`feed`, `activity`, `autoRefreshSeconds`, `mentionsOnly/showCompleted/showActivity`), it owns its own
  auto-refresh timer (`OnShown → Application.AddTimeout`, torn down in `Dispose`) and raises four events
  — `RefreshRequested`, `ToggleCompletedRequested`, `ToggleActivityRequested`, `OpenTaskRequested` —
  plus the base `Screen` `HelpRequested`/`Closed`/`FlashRequested`. It needs **no** services injected.
- `TodoApp`'s feed host logic to mirror: `CreateFeedScreen` (wiring), `RefreshFeed` (off-thread fetch +
  coalescing + `FeedCache.Save`), `ToggleFeedShowCompleted` (persist + re-fetch), `ToggleFeedShowActivity`
  (persist + local re-render). `includeClosed = _config.FeedShowCompleted`; `cacheKey = FeedCache.KeyFor(_config)`.
- `FeedService.LoadFeedAsync(includeClosed, mentionsOnly:false)` → `FeedResult(Comments, Activity)`.
- `FeedCache`: `LoadSnapshot(config)` (warm instant-paint), `Save(key, comments)`.
- Cross-process nudge channel (#294/#295): `ChangeMarkerConsumer.Advance(markers, isInView, heldVersion)`
  returns the distinct task ids to re-fetch. `SingleTaskNudgePolicy` is the single-surface analogue.

## Open question (issue) → decision

> Does dashboard `Ctrl+E` keep opening the in-dashboard feed, or become a `--feed` launch?

**Decision: keep `Ctrl+E` unchanged** (in-dashboard feed screen), add `--feed` as an **additional**
standalone launch path. This is the "safe answer" the issue names, keeps blast radius zero on the
dashboard's byte-identical render, and defers unifying the two into one path — and honoring the
split-pane destination ladder for the feed's "open a task" gesture — to the pane slices of #502.
Recorded here per the issue's request to decide it explicitly rather than by accident.

## Scope

### In scope (this PR)
- `--feed` flag parsed alongside `--task`, with `--help`/usage text and a short README note.
- A `FeedApp` root host over the shared services (`FeedService`, `FeedCache`, `AppConfig`, `ConfigStore`)
  mounting `NotificationsFeedScreen` as its root, with its own screen set + lifecycle, mirroring
  `SingleTaskApp` (diffing backend, `Application.Init`/`Run`, `ShowScreen`/`CloseScreen`, Help F1,
  exit-confirmation on root close).
- Feed fetch/refresh/toggle owned by the host (near-clone of `TodoApp`'s `RefreshFeed`/`ToggleFeed*`),
  reusing `FeedCache` warm instant-paint on boot when a snapshot exists.
- **Terminal title from feed state** (#425 refresh-on-update): `TerminalTitle.ForFeed(mentionCount)` /
  `RetitleFeed(current, mentionCount)` — pure, unit-tested; the host reassigns `Window.Title` on each
  `UpdateFeed` only when it changed.
- The feed's **"open a task from an entry" gesture launches `--task`** in a new terminal tab (reusing
  `AppLaunchCommand.ForTask` + `AppTabLaunch` + `TerminalLauncher` with the copy-command fallback) —
  matching the issue's "launches `--task`, honours the destination machinery" intent for the
  no-pane-yet slice; the full window/tab/split ladder rides the pane slices.
- Background refresh + cross-process nudge channel wired as the dashboard does: the feed is a
  cross-task aggregate, so any external marker triggers one coalesced feed refresh
  (`Advance(markers, _ => true, _ => null)` — an aggregate has no single held version to suppress on),
  keeping a feed host current cross-process on top of its own auto-refresh cadence.

### Out of scope (deferred, noted in PR)
- Any change to what the feed *shows* or how it aggregates (hosting change only).
- Unifying dashboard `Ctrl+E` with the `--feed` host, and a dedicated standalone-feed footer/help set
  (the inherited `Ctrl+E → list` / `Esc → back` both route to exit-confirmation in the standalone host,
  since there is no list — documented; a distinct footer is a trivial follow-up).
- Honoring the split-pane / window destination ladder for the "open a task" gesture — rides #502's pane
  slices (#504/#508/#515). `AppLaunchCommand.ForFeed()` (open a *feed* in a new tab) is not needed yet.

## Phases

- **Phase 1 — pure + parse (CI-testable):** `FeedLaunchArg` (`--feed` presence) + tests;
  `TerminalTitle.ForFeed`/`RetitleFeed` + tests. Build + test green → opens the draft PR.
- **Phase 2 — host + wiring:** `Tui/FeedApp.cs`; `Program.cs` `--feed` branch + `--help` text + hoist
  `feedService` construction ahead of the launch-mode early-returns; README note; E2E harness `E2E_FEED`
  branch. Build + test green.
- **Phase 3 — TUI validation:** `feed_launch_check.py` (boot `--feed` host, assert feed rows render, F3
  mentions filter, Esc → exit-confirmation), run `tui-validate`; confirm existing `feed_check.py` and the
  detail A/B checks are unaffected.

## Hard-rules check
- No `Generated/` hand edits; no `clickup-openapi.json` change / no Kiota regen — pure C# host + wiring.
- Single sectioned `ListView` model preserved: `NotificationsFeedScreen` already hosts exactly one
  focusable pane; the host adds no second focusable pane. No new bare-letter keybinding.
- Integration tests stay `SkippableFact`; new unit tests are pure and env-independent.
