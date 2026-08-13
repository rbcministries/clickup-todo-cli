# Spike: native Terminal.Gui modals for transient overlays (#404)

Exploration / spike surfaced while settling the #402 navigation taxonomy. The
question: now that the ANSI renderer has the `DiffFlushAnsi` diff-flush path, the
`TuiTeardown` dispose guard, and general `tui-validate`-hardened driver code, are
Terminal.Gui **native modals** (a nested `Application.Run(dialog)`) fast enough to
host the #402 **transient-modal** category (Help / Settings / Filter·Sort·Group /
Quick Updates / New Task / Quick Open / Agent Run / Prompt Template Editor) —
letting the bespoke `_screens` LIFO host shrink to only the true **destination**
stack (Task Detail)?

This spike does **not** migrate anything. It ships a flag-gated prototype of one
representative modal and an A/B measurement so #402 can decide (a) keep the custom
`_screens` host or (b) migrate transient modals to native surfaces.

## Background: why native modals were rejected before (#3 / #38)

Every screen today — modals included — is a hand-rolled `Screen : FrameView`
mounted on the single `_window` toplevel via `TodoApp.ShowScreen`/`CloseScreen`,
all driven by **one** `Application.Run(_window)` loop. Native `Dialog` /
`Application.Run(modalToplevel)` / `MessageBox` were rejected because a **nested
run-loop** was slow to respond and fought the latency invariants: the single
focusable `ListView` (#3) and no second run-loop competing with the background
refresh (#38). The codebase has had **zero** nested run-loops or native dialogs
ever since (verified: the only `Application.Run` calls are the two single outer
loops in `TodoApp.Run` and `SingleTaskApp.Run`).

Since that rejection, three things changed that *might* remove the original
blockers:

- `DiffFlushAnsi` — row-atomic frame diffing (~0.9 KB per redraw vs ~18.5 KB
  stock).
- `TuiTeardown.DisposeSwallowingTeardownBug` — the guard around Terminal.Gui
  2.4.10's tabbed-view dispose bug (#346).
- Driver/output hardening validated under the `tui-validate` PTY harness.

## What the prototype is

A **flag-gated** native-modal variant of the **F1 Help** screen — the simplest
transient modal (a single non-focusable `Label` + one Esc/Enter key handler, no
result to marshal back), so the measurement isolates the *nested-run-loop*
variable rather than form-input complexity.

- New env flag **`CLICKUP_TODO_NATIVE_MODAL`** (any non-empty value). Off by
  default ⇒ production is byte-identical; `ShowHelp()` still mounts the
  `_screens` `HelpScreen`.
- When set, `ShowHelp()` opens Help as a native `Dialog` via a nested
  `Application.Run(dialog)`, deferred out of the keypress with `Application.Invoke`
  (mirroring how `ShowScreen` defers teardown), and disposed through
  `TuiTeardown.DisposeSwallowingTeardownBug` so the prototype exercises the same
  teardown guard a real migration would need.
- The help text is extracted to a shared `HelpScreen.ShortcutsText` constant so
  the native `Dialog` and the `_screens` `HelpScreen` render the *same* content —
  the A/B differs only in the hosting mechanism, not the payload.

Help isn't a focusable-input surface, so it cannot by itself measure *intra-modal*
input latency — but it decisively answers the **architectural viability**
questions the #402 decision hinges on: does the nested loop paint promptly, close
cleanly (no #346 crash), leave the outer list responsive, and let the background
refresh keep ticking. A focusable-form modal (Filter·Sort·Group) is the
recommended follow-up validation if this passes, and is noted as such in the
findings.

## Measurements (A = `_screens` HelpScreen, B = native `Dialog`)

Run under the `tui-validate` PTY harness (ANSI driver, diff-flush on) via a new
`native_modal_spike_check.py`:

1. **Open→paint latency** — F1 → the help text is visible. A vs B.
2. **Bytes to open** — output volume of the F1 open. A vs B.
3. **Close correctness + dispose safety** — Esc closes the overlay, the task list
   is restored, and the process is still alive (the nested-run dispose did not
   trip #346).
4. **Outer responsiveness after a modal cycle** — after open+close, a Down-arrow
   still redraws the list within the `drive.py` latency baseline (~50 ms median;
   investigate sustained > 150 ms). This is the direct #3 invariant check.
5. **Refresh liveness while the modal is up** (observational) — `RefreshService`
   runs on a background `Task` thread and marshals via `Application.Invoke`, which
   the nested loop's shared MainLoop still pumps, so refresh is *expected* to keep
   ticking; recorded as an observation, not a hard gate.

## Deliverable

- `docs/plans/native-modals-spike.md` (this file), updated with the measured A/B
  grid and a **go/no-go recommendation** for the #402 transient-modal category.
- The flag-gated prototype (`Tui/NativeModalSpike.cs` + the `ShowHelp` branch +
  the shared help-text constant).
- `tests/ClickUpTodo.Tui.E2E/native_modal_spike_check.py` — the A/B instrument,
  which doubles as a regression guard for the prototype path.

## Hard rules

- No `Generated/` hand edits; no `clickup-openapi.json` change / no Kiota regen —
  pure TUI/host code + a harness check.
- Single sectioned `ListView` model untouched; the flag defaults off so no new
  behaviour ships enabled. The prototype adds no bare-letter binding (F1 is a
  function key; bare letters stay reserved for type-ahead #12).
- The nested-run dispose is routed through the shared `TuiTeardown` guard.

## Results

Measured under `native_modal_spike_check.py` (ANSI driver, diff-flush on, `E2E_TASKS=200`,
`ROWS×COLS = 50×200`), two F1/Esc modal cycles per host, list-nav latency as the median of five
Down-presses after the cycles. Representative run:

| metric                         | A — `_screens` HelpScreen | B — native `Dialog` (nested `Application.Run`) |
| ------------------------------ | ------------------------: | ---------------------------------------------: |
| F1 open→paint (ms)             |                      ~194 |                                           ~272 |
| F1 open bytes                  |                    ~15.1 KB |                                       ~19.0 KB |
| Esc close bytes                |                    ~19.6 KB |                                       ~19.7 KB |
| post-modal list-nav (ms, med.) |                       ~66 |                                            ~67 |
| two open/close cycles, process alive | yes                 |                                            yes |

(Absolute open/paint figures are inflated vs the `drive.py` ~50 ms steady-state baseline because
`send_measured` waits for the paint marker across a full-frame repaint under a loaded PTY; the
**A-vs-B delta** is the signal, not the absolute value.)

### What the numbers say

1. **No dispose-bug crash.** The native `Dialog` opens and closes on a nested `Application.Run`
   **twice** per leg with the process alive throughout — the Terminal.Gui 2.4.10 tabbed-view dispose
   bug (#346) does **not** trip for this modal when the dispose is routed through
   `TuiTeardown.DisposeSwallowingTeardownBug`. This was the sharpest historical blocker and it is
   gone for the non-tabbed case.
2. **No steady-state responsiveness regression (the #3 invariant).** Post-modal list-nav is **~66 ms
   (A) vs ~67 ms (B)** — indistinguishable, both near the `drive.py` baseline. A nested run-loop
   entered and exited for a transient modal leaves the single-`ListView` navigation exactly as snappy
   as the hand-mounted screen does. The #38 fear — a second run-loop leaving the outer app degraded —
   did **not** reproduce.
3. **Background refresh keeps ticking (by construction; not directly measured).** `RefreshService`
   runs on a background `Task` thread and marshals via `Application.Invoke`; the nested loop pumps the
   same MainLoop, so queued UI updates still land while the modal is up. The check does not probe for
   a refresh landing mid-modal — what it *observes* is that no hang or deadlock occurs across the
   cycles (process alive, list responsive after). Refresh-liveness is therefore reasoned from the
   architecture, not asserted; a direct probe is listed under "remains unverified".
4. **Modest, one-time open cost.** Native adds **~78 ms** to the open and **~3.9 KB** of one-time
   paint (the dialog's border/title box drawn over the full frame). Opening any full-frame overlay is
   an `IsFullInvalidation` event that diff-flush can't trim, so both hosts pay ~15–19 KB to open
   regardless; the delta is the chrome. This is a per-open cost, not a per-keypress one, so it does
   not touch the latency invariants.

## Recommendation for #402

**Go — native modals are viable for the transient-modal category; recommend option (b) migrate,
gated on one confirming measurement.**

The two reasons native modals were rejected — the nested-run-loop input-latency regression (#3/#38)
and the dispose-teardown crash (#346) — **do not reproduce** on Terminal.Gui 2.4.10 with the current
ANSI renderer path, for a non-focusable transient modal. The only cost is a modest one-time open
latency/byte bump that lands nowhere near the per-keypress invariants this repo defends.

**Caveat before committing the migration.** F1 Help is a *non-focusable* surface (a static label), so
this spike proves the nested-run-loop *architecture* is sound but does **not** exercise *intra-modal
input latency* — typing in a `TextField`, `Tab`-cycling `ListView`s/`Button`s inside the modal. The
#3 concern is fundamentally about focusable-input responsiveness. Before migrating the *form* modals
(Filter·Sort·Group, Quick Updates, New Task, Prompt Template Editor), repeat this A/B on a
**focusable** native modal — Filter·Sort·Group (F3) is the ideal candidate: it already has an E2E
check (`fsg_check.py`) and returns a `Result`, so a native-`Dialog` variant behind the same flag would
measure key→paint latency *inside* the modal against the current `_screens` form. If that holds, the
transient-modal half of the #402 taxonomy can move to native surfaces and the bespoke `_screens` host
can shrink to the destination back-stack (#346's `ScreenStackHost` scope) as #402 hoped.

**What remains unverified (explicitly):** intra-modal focusable-input latency; result-marshalling from
a native modal back to the host (Help returns nothing — F3/QU return a `Result`); a background refresh
actually landing while the modal is up (reasoned from the shared MainLoop, not probed); the `windows`
and `dotnet` drivers (this harness is ANSI-only per CLAUDE.md); and modal-stacking (F1 Help *over* an
open F3 modal, which the `_screens` host supports today via `HelpRequested`).

## Follow-up: #554 focusable-form A/B (Filter·Sort·Group) — RESOLVED

The **focusable-form native-modal A/B** the caveat above called for — the gate between this spike's
"architecture is sound" and #402 committing the transient-modal migration — is now done (#554). It
repeats the A/B on **Filter·Sort·Group (F3)**, a real focusable form (field/operator `ListView`s, a
value `TextField`, Add/Remove/Save/Cancel/Reset `Button`s) that returns a `ViewSettings?`, so it
measures the two axes F1 Help could not: **intra-modal focusable-input latency** and
**result-marshalling** back to the host.

The form is built once by a shared `FilterSortGroupFormBuilder` and hosted either as the `_screens`
`FilterSortGroupScreen` (A) or a native `Dialog` on a nested `Application.Run` (B, same
`CLICKUP_TODO_NATIVE_MODAL` gate); the A/B differs only in the hosting mechanism. Measured under a new
`fsg_modal_check.py` (ANSI driver, diff-flush on, `E2E_TASKS=200`, `ROWS×COLS = 50×200`).
Representative run:

| metric                          | A — `_screens` form | B — native `Dialog` |
| ------------------------------- | ------------------: | ------------------: |
| F3 open→paint (ms)              |                ~215 |                ~270 |
| **intra-modal key→paint (ms)**  |             **~43** |             **~44** |
| post-modal list-nav (ms, med.)  |                 ~65 |                 ~64 |

Both hosts additionally passed, per leg: open → type into the value `TextField` → **Save marshals a
`ViewSettings` back** (the host flashes the view summary and the applied filter empties the list) →
**Cancel/Esc discards** (null result, no persist) → **F1 stacks Help over the F3 modal**, Esc returns
to it → close, process alive throughout.

### What the numbers say (the axes #404 could not measure)

1. **Intra-modal focusable-input latency does not regress — the crux #3 concern.** Typing a character
   into the modal's `TextField` paints in **~43 ms (A) vs ~44 ms (B)** — indistinguishable, both at the
   `drive.py` steady-state baseline. The nested run-loop the #3/#38 invariants were written against does
   **not** make *input inside a focusable modal* any slower than the hand-mounted form. This is the
   measurement the #404 Help spike explicitly deferred, and it comes back clean.
2. **Result-marshalling works from a native modal.** Save inside the nested `Dialog` marshals the edited
   `ViewSettings` back through `ApplyViewSettings` (view summary flashed, filter applied); Cancel yields
   null and changes nothing. Help returned nothing, so this path was untested until now — it is sound.
3. **No steady-state regression (the #3 outer invariant), same as #404.** Post-modal list-nav is
   **~65 ms (A) vs ~64 ms (B)** — a focusable form modal cycle leaves the single-`ListView` navigation
   exactly as snappy as the `_screens` screen.
4. **Same modest one-time open cost as #404.** Native adds ~55 ms to the F3 open (dialog chrome over a
   full-frame repaint) — a per-open cost, not per-keypress, landing nowhere near the invariants.

### Updated recommendation for #402 — GO (caveat cleared)

**Go — migrate the #402 transient *form*-modal category (Filter·Sort·Group, Quick Updates, New Task,
Prompt Template Editor) to native surfaces.** The #404 spike proved the nested-run-loop architecture
sound but only on a non-focusable surface; #554 now confirms the remaining unknowns — **intra-modal
focusable-input latency and result-marshalling** — hold for a real focusable form. The two historical
blockers (#3/#38 input regression, #346 dispose crash) do not reproduce on Terminal.Gui 2.4.10 with the
current ANSI renderer, for either a non-focusable *or* a focusable modal. Native Help also **stacks**
over a native F3 modal, matching the `_screens` LIFO host's one stacking case.

**What remains unverified (explicitly):** the `windows` and `dotnet` drivers (this harness is ANSI-only
per CLAUDE.md — the biggest remaining gap before a full migration); a background refresh landing *while*
a focusable modal is up (reasoned from the shared MainLoop, not probed — same as #404); and modals that
themselves stack multiple focusable forms. A migration PR should carry the native variant behind the
flag until the `windows`/`dotnet` drivers are confirmed, then flip the default.

The `FilterSortGroupFormBuilder` extraction (host-agnostic form) is the shape a real migration would
reuse: keep the form pure, and let the host be either `_screens` or native `Dialog`.

## Migration policy (where this GO is acted on)

This spike's GO is now bound to a written migration policy in the accepted navigation ADR,
[`docs/navigation-model.md`](../../navigation-model.md) ("Transient-modal migration to native
Terminal.Gui modals", recorded by #614) — so the GO and the policy sit one hop apart. The ADR names
the **pilot** (Quick Open, `Ctrl+O` — the smallest form of the category, slice **E** / #618), the
**order** (pilot → the #554-validated form modals → the simple non-form modals), and the
**default-flip condition** (keep every native variant behind `CLICKUP_TODO_NATIVE_MODAL`, off by
default, until the `windows` and `dotnet` drivers are confirmed — the ANSI-only-harness gap this spike
flagged as the largest remaining unknown — which is slice **D** / #617).
