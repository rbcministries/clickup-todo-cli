# ADR: the navigation model — destinations vs. transient modals, and one `Esc` contract

**Status:** accepted · **Decision issue:** [#402](https://github.com/rbcministries/clickup-todo-cli/issues/402)
(closed; feed classification finalized in its closing comment) · **Debt retired by:**
[#614](https://github.com/rbcministries/clickup-todo-cli/issues/614)
· **Part of:** multi-tab epic [#292](https://github.com/rbcministries/clickup-todo-cli/issues/292)
· **Builds on:** [#298](https://github.com/rbcministries/clickup-todo-cli/issues/298)/[#401](https://github.com/rbcministries/clickup-todo-cli/issues/401)
(`NavigationHistory<T>` + `RequestExit()`)

This is the "single documented navigation model" #402's acceptance criteria call for: a per-screen
classification plus one uniform `Esc` contract. It is now **accepted** and describes shipped
behaviour — every rule below is checkable against the current hosts (`TodoApp`, `SingleTaskApp`,
`FeedApp`). Where a piece of the model is landed as a mechanism but not yet wired, that is called out
explicitly rather than asserted as live.

**Realized by (shipped, verifiable):** the per-host view-stack (`TodoApp._screens`, the `_stack` in
`SingleTaskApp`/`FeedApp`) with uniform `Close`/restore; the exit-confirmation seam
([#299](https://github.com/rbcministries/clickup-todo-cli/issues/299) `ExitConfirmScreen`, opt-out via
[#407](https://github.com/rbcministries/clickup-todo-cli/issues/407) `ConfirmOnExit`); the feed as a
first-class application host ([#509](https://github.com/rbcministries/clickup-todo-cli/issues/509)
`FeedApp`, `--feed`); and feed-launched task opening
([#115](https://github.com/rbcministries/clickup-todo-cli/issues/115)). `NavigationHistory<T>`
([#401](https://github.com/rbcministries/clickup-todo-cli/issues/401)) is landed as the *mechanism* for
the forthcoming multi-step detail→detail history but has **no live call site yet** — its dashboard
consumer ([#291](https://github.com/rbcministries/clickup-todo-cli/issues/291)) is still pending, so the
live back-path today is the `_screens` LIFO walk (see "The back-stack today", below).

## Decision in one paragraph

Adopt a **taxonomy, not a blanket conversion.** Two things are *destinations*: an **application host**
(a root you launch into — the dashboard's list in `TodoApp`, the launch task in `SingleTaskApp` via
`--task`, and — since #509 — the **feed** in `FeedApp` via `--feed`), and, *within* a host, exactly one
stacked surface: **Task Detail**, a genuine back location in the task chain. Every other stacked screen
(Help, Settings, Filter/Sort/Group, Quick Updates, New Task, Quick Open, Agent Run, Prompt Template
Editor, and the in-dashboard `Ctrl+E` feed overlay) is a *transient modal* that rides the host's
view-stack only and dismisses to whatever is beneath it. `Esc` obeys one three-rule contract evaluated
top-down (dismiss modal → walk the back-stack → `RequestExit()`), and the top-most **destination** on
the stack is always the current back location, with modals transparent to it.

## Two levels of "destination"

The word *destination* lives on two axes; keeping them apart is what makes the taxonomy consistent.

- **Application host (a root).** A process entry point with its own run-loop and view-stack: the
  dashboard (`TodoApp`), a single task (`SingleTaskApp`, `--task`), and the feed (`FeedApp`, `--feed`,
  #509). This is the sense in which **the feed is a navigation destination, not a modal** — #402's
  closing decision. A host is never "dismissed to something beneath it"; it is the bottom of its own
  stack, and closing its root hands off to `RequestExit()`.
- **In-stack destination (within a host).** A stacked surface that is a real back location, recorded in
  the host's back-stack. **Only Task Detail qualifies.** Everything else stacked is a transient modal
  that dismisses to the layer beneath and is never a back location.

The feed appears on *both* axes, by explicit design (#509, `feed-application-host.md`): as the `--feed`
**host** it is a destination root; as the in-dashboard **`Ctrl+E` overlay** it is a transient modal over
the list. Both were kept deliberately — see "The feed", below.

## The taxonomy (every screen)

| Screen | Trigger | Category | `Esc` behavior | Back location? |
|---|---|---|---|---|
| `TaskDetailScreen` | Enter on list; detail→detail (`OpenTaskRequested`); Ctrl+O resolve; feed row | **Destination** (in-stack) | Back — pop to the layer beneath (a previous detail, a transient modal beneath it such as the `Ctrl+E` feed overlay, or the host root) | **Yes** — the one recorded in-stack destination |
| `HelpScreen` (F1) | over any screen | Modal | Dismiss to layer beneath | No |
| `SettingsScreen` (F10) | list | Modal | Cancel (discard edit) | No |
| `FilterSortGroupScreen` (F3) | list | Modal | Cancel (`Result` null) | No |
| `QuickUpdatesScreen` (Ctrl+U) | list / over detail | Modal | Cancel | No |
| `NewTaskScreen` (Ctrl+N) | list | Modal | Cancel | No |
| `QuickOpenScreen` (Ctrl+O) | list / over detail | Modal (entry surface) | Cancel — the resolved task is the destination, not the surface | No (the task it resolves is pushed) |
| `AgentRunScreen` | over detail | Modal | 1st Esc cancels the in-flight run; when finished, Esc closes | No |
| `PromptTemplateEditorScreen` | over Settings | Modal (nested) | Cancel | No |
| `NotificationsFeedScreen` | `--feed` host (#509); in-dashboard `Ctrl+E` overlay | **Destination** as the `--feed` host; **transient modal** as the `Ctrl+E` overlay | host root → exit-confirm; overlay → back to the list (see "The feed") | As host: it *is* a root. As overlay: No |
| `ExitConfirmScreen` (#299) | `RequestExit()` — contract rule 3 (and rule 2 at `SingleTaskApp`'s / `FeedApp`'s root) | Modal | Cancel — dismiss to the root view beneath | No |

## The back-stack today

The live back-stack is the host's **`_screens` LIFO view-stack**, not `NavigationHistory<T>`.
`ShowScreen` hides the current layer and appends (`TodoApp.ShowScreen`); `Close` on the top screen
raises `Closed`, and `CloseScreen` pops it and restores the layer beneath (`below.Visible = true;
below.SetFocus()`, or the list frame when the stack empties). A task is pushed at the single
`OpenTaskDetail` site (`ShowScreen(screen)`), which is shared by every open path — list, tree, Ctrl+O,
**and the feed** — so "opened from the feed" differs only in which layer sits beneath the detail, not in
how it is pushed.

`NavigationHistory<T>` (#401) is the *intended* backing store for the multi-step detail→detail history,
but it has **no live call site** (verified: no `new NavigationHistory<…>` anywhere in `src`); its
dashboard consumer is #291. Until #291 lands, the observable back behaviour is the single-step `_screens`
walk described above — which already satisfies the contract for the single-step case. When the column
above says a detail is the "back location", that is realized by `_screens` today.

## The `Esc` / `RequestExit` contract

One chokepoint per host, evaluated top-down:

1. **A transient modal is on top of the view-stack** → `Esc` closes that modal (`Screen.Close()`),
   restoring the layer beneath. The back-stack is untouched. (Covers overlays over a detail — Ctrl+U,
   F1, the Ctrl+O surface — which must not disturb the task back-path.)
2. **A destination (detail) is on top** → `Esc` pops it and shows the layer beneath — whatever it is: a
   previous detail, a transient modal beneath it (the `Ctrl+E` feed overlay, when the detail was opened
   from the feed — see "The feed"), or the host root. If the detail *is* the root (`SingleTaskApp`),
   `Esc` hands off to `RequestExit()`. (`CloseScreen` restores `_screens[^1]` unconditionally, so the
   "layer beneath" is literal — the model does not special-case what that layer is.)
3. **At the host root with nothing stacked** → `Esc`/quit → `RequestExit()`. Roots: `TodoApp` = list;
   `SingleTaskApp` = launch task; `FeedApp` = feed. **As of #299 this plug point is filled:**
   `RequestExit()` mounts the `ExitConfirmScreen` modal (`Y`/`Enter` exits, `N`/`Esc` dismisses back to
   the root) instead of stopping the app directly — unless the user opted out via #407
   (`ConfirmOnExit == false`), in which case it stops directly. The confirm screen is itself a transient
   modal under rule 1 — never a back location — and because it consumes every key, nothing but a
   deliberate answer can leave the app while it is up: `Y`/`Enter`, or a second press of the quit chord
   that raised it (`Ctrl+Q`/`Ctrl+C`). `Esc` there is the "no" answer, and any other chord does nothing.

**Invariant.** The top-most **destination** on the view-stack is always the current back location,
ignoring any modals layered above it. Modals are transparent to the back-path — that is the entire point
of the split, and it is what lets the forthcoming #291 history stay a faithful mirror of the destination
back-path without teaching it about every overlay.

## The feed

Since #509 the feed has two realizations, and #402's closing comment settles its headline
classification: **the feed is a navigation destination, not a modal** — realized as the `--feed`
application host (`FeedApp`), a root peer of the dashboard and the single-task host. The in-dashboard
`Ctrl+E` overlay is kept unchanged as a convenience view (a transient modal over the list), by the
explicit "keep `Ctrl+E` unchanged, add `--feed` as an additional path" decision in
`docs/plans/completed/feed-application-host.md`.

- **As the `--feed` host (`FeedApp`):** the feed is the root. `Esc` (and the inherited `Ctrl+E`) route to
  `RequestExit()` → exit-confirmation, since there is no list beneath. Opening a task from an entry
  **launches `--task` in a new terminal tab** (via `AppLaunchCommand.ForTask` / `AppHostLaunch`), not an
  in-app stacked detail — the destination-host analogue of stacking.
- **As the in-dashboard `Ctrl+E` overlay:** the feed is a transient modal over the hidden list. `Esc`
  returns to the list. A task opened *from* the feed stacks over it (`[list, feed, detail]`), so `Esc`
  from that detail returns to the **feed** with its selection intact — the #115 behaviour, retained.

### Superseded: the earlier "feed-as-modal, resolving #403 case 2" proposal

The proposal stage of this ADR classified the feed flatly as a transient modal and proposed a specific
back-path fix: record the *task* opened from the feed in `NavigationHistory`, keep the *feed screen* out
of history, and have back-navigation **bypass the feed** to land on the list — an intentional change to
the #115 "Esc returns to the feed" behaviour. **That proposal is superseded and was never implemented:**

- `NavigationHistory<T>` was never wired (no call site), so the "record the task, bypass the feed"
  mechanism does not exist. In the dashboard, a feed-opened task returns to the **feed** today (the #115
  behaviour the proposal meant to change), via the plain `_screens` restore.
- #509 reframed the feed as a first-class **destination host**, which moots the "is the feed a back-stop
  or a launcher?" question the proposal was trying to answer: as a host it is a root; as the `Ctrl+E`
  overlay it is a modal that dismisses to the list. Both are coherent without a special-case in the
  history.

The feed `tui-validate` scenario therefore asserts the **shipped** contract — dashboard `Esc` from a
feed-opened detail returns to the feed; `--feed`-host `Esc` routes to exit-confirmation — not the
proposal-stage "returns to the list" that was never built. No behavioural change ships from this ADR;
it is a documentation reconciliation.

## Transient-modal migration to native Terminal.Gui modals

The transient-modal category may be re-hosted from the bespoke `_screens` LIFO stack onto **native
Terminal.Gui modals** (a nested `Application.Run(dialog)`). The viability question — historically closed
by the #3/#38 input-latency fear and the #346 dispose crash — was re-opened and answered **GO** by two
spikes, both recorded in `docs/plans/completed/native-modals-spike.md`:

- **#404** (non-focusable F1 Help A/B): no #3/#38 latency regression, no #346 dispose crash on
  Terminal.Gui 2.4.10 with the current ANSI renderer.
- **#554** (focusable-form F3 Filter·Sort·Group A/B): the remaining unknowns — intra-modal
  focusable-input latency (~43 ms A vs ~44 ms B) and result-marshalling back to the host — hold. GO,
  caveat cleared.

This ADR **owns the migration policy** (previously "#402's open call", now closed and hence ownerless —
`docs/plans/contextual-chord-model.md` §7 flagged the gap). The taxonomy itself is unchanged: only the
*hosting mechanism* of the transient-modal category moves; Task Detail (the destination) stays on the
`_screens` stack.

**Decision:**

- **Pilot: Quick Open (`Ctrl+O`, `QuickOpenScreen`).** It is the smallest form of the category — one
  `TextField`, two `Button`s (`Open`/`Cancel`), and a `string?` `Result` the host resolves after close —
  so it re-hosts with the least surface while exercising the real path (focusable input + a returned
  value). It is currently a plain `_screens` screen with no native branch, so it is a clean first
  migration.
- **Order.** (1) Quick Open (pilot). (2) The remaining focusable **form** modals #554 validated —
  Filter·Sort·Group, Quick Updates, New Task, Prompt Template Editor. (3) The simple non-form modals —
  Help, Agent Run, Exit Confirm. The F/G contextual **confirm/choice dialogs** (#543/#544) already went
  native behind the flag as the reusable `ConfirmDialog`/`ChoiceDialog` (`contextual-chord-model.md` §4),
  and are the shape the migrations above reuse — they are new dialogs, not a re-host of an existing
  `_screens` screen, so they lead rather than wait on the pilot.
- **Default-flip condition.** Every native variant ships **behind the `CLICKUP_TODO_NATIVE_MODAL` flag
  (off by default)** until the **`windows` and `dotnet` drivers** are confirmed — the `tui-validate` PTY
  harness is ANSI-only per `CLAUDE.md`, and driver parity is the spikes' single largest remaining
  unknown. That verification is the epic's slice **D**
  ([#617](https://github.com/rbcministries/clickup-todo-cli/issues/617)); when it passes, flip the
  default. Quick Open's flag-gated re-host is slice **E**
  ([#618](https://github.com/rbcministries/clickup-todo-cli/issues/618)), the pilot this ADR names.

The native spike (`NativeModalSpike`) already gates F1 Help and F3 Filter·Sort·Group behind
`CLICKUP_TODO_NATIVE_MODAL` for the A/B measurements; the migration promotes that flag from a
measurement harness into the shipping default-flip switch above.

## Consequences

- **The forthcoming #291 history** wires one back-stack per host, mirroring the `_screens` destination
  walk this ADR describes; it needs to model only the destination back-stack plus modal
  overlay/dismiss, not two competing mechanisms. `NavigationHistory<T>` (#401) is the store it will use.
- **The feed** is a destination host (`--feed`, #509) and a retained in-dashboard modal overlay
  (`Ctrl+E`); no history special-case is needed for either.
- **No behavioural change ships from this ADR.** `Esc` already walks the `_screens` stack one screen at
  a time; this document is now a faithful description of that, with the proposal-stage feed-bypass
  (never built) retired. The one migration this ADR *authorizes* — transient modals → native surfaces —
  ships flag-gated and default-off until the `windows`/`dotnet` drivers are confirmed (slice D), so it
  too is invisible until deliberately flipped.
- **Native modals** for the transient-modal category are now a **decided migration** (pilot Quick Open,
  flag-gated pending driver parity), not "an open exploration" — see the section above and
  `docs/plans/completed/native-modals-spike.md`.
