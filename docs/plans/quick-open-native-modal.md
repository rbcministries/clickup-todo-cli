# Quick-open native-modal pilot — re-host Ctrl+O as a native `Dialog` (#618)

Slice **E** of the Ctrl+O quick-open epic (#613), and the **#402 transient-modal
migration pilot**. Move the Ctrl+O quick-open surface off the hand-mounted
`_screens` host onto a native Terminal.Gui `Dialog` on a nested
`Application.Run`, **behind the existing `CLICKUP_TODO_NATIVE_MODAL` flag (off by
default)** — the same A/B shape #554 landed for Filter·Sort·Group.

This slice is a **hosting-mechanism change only**: the quick-open feature (launch
modes B, #615) is untouched. If it changes what the user can do, it has
overstepped (issue non-goal).

## Why quick-open is the pilot

Recorded in the navigation ADR (A, #614) and this issue: quick-open is the
smallest form of the transient-modal category — a prompt `Label`, one
`TextField`, a four-button row, and a `QuickOpenRequest?`-shaped result — so it
is the cheapest existing surface to migrate while still exercising the two axes
#554 measured (focusable input, result marshalling), now with B's launch intent
riding on the result.

## Dependencies (all satisfied for a flag-gated landing)

- **A (#614)** — merged (PR #621): the navigation ADR is `accepted`, names
  quick-open as the pilot, and records the default-flip condition (D).
- **B (#615)** — merged: the launch-mode gestures and the intent-shaped
  `QuickOpenRequest` result live on the surface today.
- **D (#617)** — open; the `windows`/`dotnet` driver verification. It **only**
  gates flipping the `CLICKUP_TODO_NATIVE_MODAL` default. This slice lands the
  native host flag-gated OFF and does **not** flip the default, so D is not
  blocking (per this issue's "Flipping the default" section).

## The AddTimeout simplification (the real win beyond the migration)

The `_screens` host defers the resolve one main-loop iteration
(`Application.AddTimeout(1ms)` in `ShowQuickOpenSurface`) because resolving
inline from the `_screens` close handler runs **while the modal is still
mounted**, so `OpenTaskDetail` captures the closing modal as its requester and
skips the mount ("Loading details…" stuck, observed under `tui-validate`). A
nested `Application.Run(dialog)` returns **after** teardown, so the native host
resolves straight from the marshal callback with **no deferral**. The E2E asserts
the native OpenHere leg navigates to Task Detail without the workaround.

## Design (A/B differs only in the hosting mechanism)

### 1. `QuickOpenFormBuilder` + `QuickOpenFormHandle` (`Tui/Screens/QuickOpenFormBuilder.cs`)

Extract the control-building and per-form behaviour out of `QuickOpenScreen`'s
constructor into a static builder, mirroring `FilterSortGroupFormBuilder` (#554):

- `QuickOpenFormHandle { Controls, PrimaryFocus, Result }` — the built controls
  in tab/paint order, the control to focus first (the `TextField`), and the
  B-shaped `QuickOpenRequest?` (set on submit, null on cancel).
- `Build(Action<string> flash, Action close)` builds the prompt label, the input
  field, the `Open`/`New tab`/`Split pane`/`Cancel` button row, wires each
  button's `Accepting` and the three submit gestures
  (`Enter`/`Ctrl+Enter`/`Ctrl+Alt+Enter`) through the **same** `Submit(intent)`,
  and owns the blank-input flash. `Submit` sets `handle.Result` + calls `close`
  when `QuickOpenRequest.From(...)` is non-null, else flashes and stays open.
- **Per-form keys live in the builder** (the three submit gestures, dispatched
  through a `KeybindingDispatcher(ScreenContext.QuickOpen)` attached to the input
  field); **context command keys stay on the host** (`Esc` = Back, `F1` = Help),
  exactly as FSG splits them — because the two hosts realise Back/Help
  differently (a `Screen`'s `Close`/`RequestHelp` vs. the `Dialog`'s
  `RequestStop`/native help-stack). The handle exposes `DispatchSubmit(Key)` so
  each host also wires the submit gestures at its surface level (so a chord fires
  whether the field or a button holds focus — matching the pre-extraction
  screen-level wiring).

### 2. `QuickOpenScreen` becomes a thin `Screen` (`Tui/Screens/QuickOpenScreen.cs`)

Mounts the handle's controls, focuses `PrimaryFocus` on show, forwards
`Result`, and keeps its own `Help`/`Back` dispatcher + `HelpItemSets.QuickOpen`
footer. **Leg A behaviour is byte-identical** — the existing quick-open E2E
checks stay green.

### 3. Native host (`Tui/NativeModalSpike.RunQuickOpenDialog`)

A sibling of `RunFilterSortGroupDialog`: a `Dialog` carrying the spike's
`TitleMarker` (so the harness can prove leg B took the native path), the same
builder mounted inside, a nested `Application.Run`, dispose through
`TuiTeardown.DisposeSwallowingTeardownBug`, its own `_quickOpenOpen` slot guard
(the native path pushes nothing to `_screens`, so `ActiveScreen` can't serialise
it), `Esc` → `RequestStop`, `F1` → the native help stack (deferred via
`Application.Invoke`, guarded by the help slot), and the `QuickOpenRequest?`
result marshalled back through a `resolve` callback in the `finally`.

### 4. Host wiring (`Tui/TodoApp.ShowQuickOpenSurface`)

Branch on `NativeModalSpike.Enabled` exactly as `OpenViewSettings` (`:1160`) and
`ShowHelp` (`:3407`) already do. Both Ctrl+O entry points (list + detail) go
through the one `ShowQuickOpenSurface`, so it is a single branch. The
per-intent switch is lifted into a shared `DispatchQuickOpen(QuickOpenRequest?)`
that both legs call — the `_screens` leg from inside its `AddTimeout(1ms)`, the
native leg directly from the marshal (post-teardown, no deferral).

## The footer question (settled)

The `_screens` host paints the contextual footer (`HelpItemSets.QuickOpen`),
where B's two launch gestures are advertised. A native `Dialog` sits over the
frame, and #554's native FSG variant does **not** re-home that footer onto the
dialog. That is acceptable here for the same reason: B already ships the
`New tab` / `Split pane` gestures as **buttons in the form**, so they stay
discoverable (and mouse-reachable) inside the native dialog with no footer — the
button row, not the footer, carries discoverability on the native leg. Recorded
here so E doesn't silently drop an affordance.

## Tests

- **Unit** (`QuickOpenRequestTests`, existing): the pure `QuickOpenRequest.From`
  result shape (text + intent, blank → null, trim) — the seam both hosts and the
  builder funnel submits through; unchanged by the extraction, re-affirmed.
- **`tui-validate`** `quick_open_modal_check.py` (new), modelled on
  `fsg_modal_check.py`: A (`_screens`) vs B (native, `CLICKUP_TODO_NATIVE_MODAL=1`)
  — Ctrl+O open→paint; intra-modal typing latency; **OpenHere marshals + navigates
  to Task Detail** (the AddTimeout-free native resolve); `Esc` yields no result and
  restores the list; `F1` stacks Help over the surface and `Esc` returns to it;
  post-close list-nav within budget; leg B proves native via `TitleMarker`; and the
  existing `quick_open_check.py` / `quick_open_followups_check.py` stay green with
  the flag off (leg-A byte-identical guarantee).

## Non-goals / deferred

- **Flipping the `CLICKUP_TODO_NATIVE_MODAL` default** — gated on D (#617); each
  consuming slice flips its own surface. This lands OFF.
- **Migrating the other #402 form modals** (Filter·Sort·Group already piloted the
  focusable form; New Task, Quick Updates, Prompt Template Editor) — each its own
  slice.
- No behaviour change to the quick-open feature itself.
