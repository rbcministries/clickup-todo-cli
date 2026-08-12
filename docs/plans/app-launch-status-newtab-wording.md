# App-launch status line: stop over-promising "a new tab" (#591)

Follow-up to #589 / PR #590, discovered reviewing that PR. Sibling to the
`SplitViability` degradation-message fix #590 shipped, in the app-launch surface
(`AppHostLaunch`, driving the #301 "open this app's task in its own terminal"
gesture — dashboard/single-task `Ctrl+Enter`, feed Enter).

## Problem

`AppHostLaunch`'s three destination-aware status phrases hard-code the word
**"tab"** for any `NewTab` destination:

- `OpeningPhrase(NewTab)` → `"a new terminal tab"` (`AppHostLaunch.cs`)
- `OpenedPhrase(NewTab)` → `"a new tab"`
- `FallbackPhrase(NewTab)` → `"a terminal tab"`

After #589 gave WezTerm/kitty/Zellij a real `NewTab` ladder (split → tab →
window), a `NewTab` request is no longer always a literal tab: in a Zellij-only
session it becomes an **in-session pane**, and where the tab rung isn't reachable
it falls through to a **window**. So the app-launch gesture inside Zellij would
flash e.g.

> Opened 'x' in **a new tab** (Zellij (new pane)).

— the lead noun contradicts the parenthetical, which already carries the *true*
surface via `result.LaunchedWith` (the launched `LaunchSpec.Description`, e.g.
`"Zellij (new pane)"`, `"tmux (new window)"`, `"gnome-terminal (new tab)"`).

The wording was **deliberately pinned byte-identical** to the retired
`AppTabLaunch` strings (see the note at `AppHostLaunch.cs`), so changing it is
its own small, test-touching change — out of scope for #589.

## Decision

No maintainer decision is pending — the issue spells out the direction. Adopt
the issue's own suggested wording: **soften the `NewTab` phrases to host-neutral
"… where supported"** so the status line never *asserts* a tab the host didn't
open. This is the minimal, safe change and reads correctly in every case:

- `Opening(NewTab)` → `"a new terminal tab where supported"` — said **before**
  the launch, when the resolved surface genuinely isn't known yet.
- `Opened(NewTab)` → `"a new tab where supported"` — the parenthetical
  `({result.LaunchedWith})` still names the actual surface, so
  `"Opened 'x' in a new tab where supported (Zellij (new pane))."` is
  self-consistent: the hedge no longer claims a literal tab, and the
  parenthetical says what really happened.
- `Fallback(NewTab)` → `"a terminal tab where supported"` — the no-emulator /
  launch-threw case; nothing opened, so the phrase must not promise a tab
  either.

`NewWindow` and `SplitPane` wording is **unchanged** — those destinations don't
degrade *up* into a tab, and a degrading split already tells the truth via
`result.Note` (the fell-back notice `Opened` appends) and the #590
`SplitViability` message.

## Scope

- `src/ClickUpTodo/Agent/AppHostLaunch.cs` — the three `*Phrase(NewTab)` default
  arms only; update the "pinned byte-identical" comment to record the
  intentional de-pinning and why.
- `tests/ClickUpTodo.Tests/AppHostLaunchTests.cs` — update the pinned `NewTab`
  expectations (Opening/Opened/Fallback, incl. the note/blank-note/reason
  cases); `NewWindow`/`SplitPane` expectations stay byte-identical. Add a
  regression test proving `Opened(NewTab, …)` no longer contradicts a
  non-tab `LaunchedWith` (Zellij pane / window fallback).

## Out of scope / non-goals

- The launch-ladder planner and the `SplitViability` message — already fixed in
  #590.
- The `NewWindow` / `SplitPane` phrasings.
- Any `Generated/` edit, `clickup-openapi.json` change, or Kiota regen (no
  ClickUp API surface touched).
- Any TUI/rendering change — `AppHostLaunch` is pure string logic; the three
  callers (`TodoApp`, `SingleTaskApp`, `FeedApp`) are untouched, so no
  second-focusable-pane or latency concern (#3).

## Verification

- `dotnet build -c Release` (0/0) + `dotnet test -c Release` (unit-only for this
  change; integration `SkippableFact`s skip without `CLICKUP_TOKEN`).
- `dotnet format --verify-no-changes`.
- No `tui-validate` needed: no rendering, list-source, driver, or keypress code
  is touched — only status-string composition, covered by `AppHostLaunchTests`.
</content>
</invoke>
