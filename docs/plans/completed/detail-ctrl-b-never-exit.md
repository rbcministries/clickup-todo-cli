# Ctrl+B must never exit a root view, plus an F2 close-vs-stay setting (#518)

## Problem

In the `--task` host, `Ctrl+B` (open the task in the browser) **exits the
program**. The detail view is the root of that host's screen stack, so its
`Closed` handler falls through to `RequestExit()` — but for Ctrl+B the exit is
*explicit and deliberate* (`SingleTaskApp.cs:190-200` calls
`Application.RequestStop()` after launching the browser), with a written
rationale ("open in browser and close the tab is an explicit request"). It also
bypasses the #299 exit-confirmation, and — because the view is gone — a failed
browser launch on that host is only `Debug.WriteLine`'d, never surfaced.

This issue **overturns that design decision**, not a fall-through bug.

## Decisions (settled in the issue, owner-affirmed)

1. **Invariant:** Ctrl+B must **never** reach an exit path, in any host, under
   either setting. At a host root (the `--task` launch task) there is no back to
   navigate to, so the view always stays.
2. **Default:** `KeepOpen` — Ctrl+B opens the browser and changes nothing else,
   in **both** hosts. Matches the in-screen Ctrl+click-a-task-link precedent
   (#318/#320) and makes the two hosts behave identically. This is a deliberate
   departure from the `TaskLinkCtrlClick` (#320) precedent of defaulting to
   prior-shipped behaviour — the old default is exactly what caused the bug.
3. A new `DetailViewSettings` two-value enum governs the **non-root** case only.

## Mechanism

Ctrl+B is today the *only* detail action that signals the host by setting a
`bool OpenBrowserRequested` flag and `Close()`-ing itself, scraped out of a
`Closed` handler during teardown (`TaskDetailScreen.cs:1020-1026`). Every
sibling action (`QuickUpdatesRequested`, `OpenInNewTabRequested`, …) raises an
**event** and lets the host decide. The fix makes Ctrl+B an event like its
siblings, so both parts become structural rather than special-cased:

- the host reads the setting and decides whether to navigate back, and
- a root host simply has no back to navigate to.

Nothing runs during `Closed` for the browser path any more.

## Changes

### Phase 1 — config + pure decision (test-first)

- **`Configuration/OpenBrowserBehavior.cs`** — new enum `KeepOpen` (default) |
  `CloseView`, with a `Next()` cycle extension (mirrors
  `TaskLinkCtrlClickDestination`). Lives in `Configuration` as the single source
  of truth.
- **`DetailViewSettings.OpenBrowser`** — new property, default `KeepOpen`. Absent
  key deserializes to the default (no migration; enums persist as strings via
  `StateJson`'s `JsonStringEnumConverter`).
- **`Tui/OpenBrowserAction.cs`** — pure `ShouldCloseView(OpenBrowserBehavior,
  bool isRoot)` → `!isRoot && setting == CloseView`. The one place the invariant
  and the setting compose; unit-tested (isRoot always ⇒ stay; KeepOpen ⇒ stay;
  CloseView non-root ⇒ close).

### Phase 2 — screen + hosts

- **`TaskDetailScreen`** — replace `public bool OpenBrowserRequested` with
  `public event EventHandler? OpenBrowserRequested`; the Ctrl+B handler raises it
  and no longer `Close()`s.
- **`TodoApp.OpenTaskDetail`** — subscribe: launch the browser (flashing on the
  live view via the existing `LaunchBrowser`), then `screen.Close()` only when
  `OpenBrowserAction.ShouldCloseView(setting, isRoot: false)` — a dashboard
  detail always has the main list beneath, so it is never root. Drop the flag
  check from the `ShowScreen` `Closed` callback.
- **`SingleTaskApp` root** (`:190-200`) — subscribe to the event: launch +
  **flash** (the root now survives, so it is a live view), `isRoot: true` ⇒ never
  close, **remove `Application.RequestStop()`**. The `Closed` handler keeps only
  the Esc → `RequestExit()` path. Repurpose the `Debug.WriteLine`-only
  `LaunchBrowser(url)` to flash the three outcomes (like `OpenLink`), and update
  the now-stale comments at `:181-188` and `:782-786`.
- **`SingleTaskApp` child** (Task Tree tab, `:385-391`) — a stacked child is not
  root, so honour the setting: launch + flash, close only in `CloseView`.

### Phase 3 — F2 surface + tests + docs

- **`SettingsScreen`** — add an On/Off-style cycle button in the Detail-view
  column (after `TaskLinkCtrlClick`, Y=14; shift General header/confirm down to
  15/16), threaded into the `DetailViewSettings` the Save builds. New
  `OpenBrowserText` helper.
- **Tests** — `OpenBrowserActionTests` (the decision truth table incl. the
  root-invariant), extend `DetailViewSettingsTests` (default, `Next()`,
  round-trip, persisted-as-string, absent-key default). The Terminal.Gui host
  wiring is verified by build + reasoning + `tui-validate` per CLAUDE.md.
- **README** — a release-notes line for the close→keep-open behaviour change and
  the new F2 setting.

## Non-goals / unaffected

- `Ctrl+B` on the **main list** — a different action on a different screen.
- Esc's exit path and its #299 confirmation — untouched; only Ctrl+B stops
  reaching an exit.

## Verification

- `dotnet build -c Release` (0/0), `dotnet test -c Release` green,
  `dotnet format` clean.
- `tui-validate`: `--task` host survives Ctrl+B (browser + stays);
  `detail_check.py` A/B byte-identical (the setting is off-path until Ctrl+B).
