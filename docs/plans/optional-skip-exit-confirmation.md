# Optional preference to skip the exit confirmation

Issue: [#407](https://github.com/rbcministries/clickup-todo-cli/issues/407) (follow-up to
[#299](https://github.com/rbcministries/clickup-todo-cli/issues/299), multi-tab epic
[#292](https://github.com/rbcministries/clickup-todo-cli/issues/292) sub-issue 7). Deferred
from PR #406 and named as the explicit *Out of scope* item of the #299 plan
(`docs/plans/completed/exit-confirmation-modal.md`): "a preference to skip it is a separate
decision".

## Goal

Let a user who quits and relaunches often — especially with several single-task tabs open
(#301/#296) — opt out of the exit-confirmation modal and get the old one-key exit back, while
keeping the confirmation **on by default** (the right default per #290: it guards the one
genuinely destructive `Esc`). The opt-out is a persisted preference, editable in the F2
Settings dialog, and it applies identically in both launch modes.

## Why this shape

- **`RequestExit()` is still the single chokepoint.** #298/#299 funnelled every root-level
  quit path through `TodoApp.RequestExit()` and `SingleTaskApp.RequestExit()`, and the modal
  lands entirely inside those two methods. The opt-out is a one-line guard at the top of each:
  when the preference is off, `Application.RequestStop()` directly instead of mounting the
  modal. No new key wiring, no new Esc paths, and "consistent across launch modes" stays free
  because both hosts share the same seam.
- **A single persisted `bool` on `AppConfig`, defaulting to `true`.** `ConfirmOnExit = true`
  mirrors the existing `RefreshSeconds = 60` / `FeedRefreshSeconds = 300` precedent: an absent
  key in an older `config.json` deserializes to the C# initializer value (System.Text.Json
  only overwrites keys that are present), so **existing users keep the guard with no
  migration**. This is why the preference is modelled as `ConfirmOnExit` (default `true`)
  rather than an inverted `SkipExitConfirmation` — the positive-default bool is the
  backward-compatible shape the codebase already relies on.
- **Edited in the F2 Settings dialog**, as a cycle-button toggle mirroring the existing
  "Default post to Comments" / launch-location toggles — no new UI machinery, and the toggle
  is threaded through the existing `SettingsResult` transaction boundary (an F2 Cancel discards
  it, Save persists it via `ConfigStore`).
- **The pure `ExitConfirmModel` is untouched.** The modal's yes/no answer logic doesn't change;
  only *whether the modal is shown* does, and that decision reads a single bool at the host
  seam. Keeping the model a pure yes/no keeps the well-tested #299 answer rules intact.

## Scope (this PR)

### 1. `Configuration/AppConfig.cs` — the persisted preference

- `public bool ConfirmOnExit { get; set; } = true;` with a doc comment recording the
  default-true backward-compat contract (absent key ⇒ `true`, like `RefreshSeconds`).

### 2. Both hosts' `RequestExit()` (`Tui/TodoApp.cs`, `Tui/SingleTaskApp.cs`)

```
RequestExit():
    already confirming? → no-op            (unchanged re-entrancy guard)
    !_config.ConfirmOnExit? → Application.RequestStop()   (new: skip the modal)
    else → ShowScreen(new ExitConfirmScreen(), …)          (unchanged)
```

The re-entrancy guard stays first so the new branch can't fire while a modal is already up
(it can't be, but the ordering keeps the invariant obvious). Documented at both call sites.

### 3. F2 Settings toggle (`Tui/Screens/SettingsScreen.cs`)

- New ctor param `bool confirmOnExit`; a `confirmOnExitButton` cycle toggle
  ("Confirm on exit: On/Off") in the left column under the Detail-view section; `ConfirmOnExit`
  added to the `SettingsResult` record.
- `TodoApp.OpenSettings` passes `_config.ConfirmOnExit` in and writes `result.ConfirmOnExit`
  back before `_configStore.Save(_config)`. Read live by `RequestExit` on the next quit, so a
  saved change takes effect immediately with no extra wiring.

### 4. Help / docs copy

- README quit row + "Quitting asks first" paragraph, and the F1 `HelpScreen` quit line, note
  the guard is **on by default** and can be turned off in F2 Settings.

## Tests

- **Unit (`ConfirmOnExitConfigTests`):** `ConfirmOnExit` defaults to `true` on a fresh
  `AppConfig`; a `config.json` **without** the key loads as `true` (the backward-compat
  invariant — the crux of this issue); an explicit `false` round-trips through `ConfigStore`
  save/load; the value persists as a JSON bool (`"confirmOnExit": false`), never an ordinal.
  Mirrors `DetailViewSettingsTests`' persistence pattern.
- **Unit (`SettingsScreenResultTests` or an addition to the settings tests):** a `SettingsResult`
  carries `ConfirmOnExit` through unchanged — the record is the pure transaction object the F2
  glue populates.
- **TUI (build + reasoning; existing `exit_confirm_check.py` unchanged):** the default-on path
  is already covered by the #299 `exit_confirm_check.py` and `single_task_launch_check.py` (the
  preference defaults on, so their assertions are unaffected). The off path — `Esc` exits with
  no modal — is a one-line host branch verified by build + reasoning; the PR describes how a
  maintainer can confirm it by toggling F2 → Save → Esc.

## Out of scope

- **A per-launch CLI flag (`--yes`).** #407 offers the preference *and/or* a CLI flag; the
  persisted preference fully satisfies the ask (a scripted/dispatch launch can pre-seed
  `confirmOnExit: false` in its `config.json`), so no new `Program.cs` surface is added here.
- **An inline "don't ask again" answer in the modal.** #407 raises it as an open question; the
  F2 toggle is the single, discoverable surface for the preference, and adding a third answer
  would push a persistence side-effect into the deliberately pure `ExitConfirmModel`. Left out
  by design.
- Any change to the modal's yes/no logic, to `NavigationHistory`, or to the exit key wiring.
