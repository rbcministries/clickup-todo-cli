# ADR: the navigation model — destinations vs. transient modals, and one `Esc` contract

**Status:** proposed · **Decision issue:** [#402](https://github.com/rbcministries/clickup-todo-cli/issues/402)
· **Part of:** multi-tab epic [#292](https://github.com/rbcministries/clickup-todo-cli/issues/292)
· **Builds on:** [#298](https://github.com/rbcministries/clickup-todo-cli/issues/298)/[#401](https://github.com/rbcministries/clickup-todo-cli/issues/401)
(`NavigationHistory<T>` + `RequestExit()`) · **Implemented by:**
[#403](https://github.com/rbcministries/clickup-todo-cli/issues/403) (first host consumer),
[#346](https://github.com/rbcministries/clickup-todo-cli/issues/346) (shared `ScreenStackHost`)

This is the "single documented navigation model" #402's acceptance criteria call for: a per-screen
classification plus one uniform `Esc` contract, so that #403 can wire `NavigationHistory` against a
settled target and #346's `ScreenStackHost` can wrap the settled seam rather than a moving one.

## Decision in one paragraph

Adopt a **taxonomy, not a blanket conversion.** Exactly one surface — **Task Detail** — is a
*navigation destination* recorded in `NavigationHistory<T>`; every other screen (Help, Settings,
Filter/Sort/Group, Quick Updates, New Task, Quick Open, Agent Run, Prompt Template Editor, **and the
Notifications Feed**) is a *transient modal* that rides the `_screens` view-stack only and is
**never** recorded in history. `Esc` obeys one three-rule contract evaluated top-down (dismiss modal
→ walk history → `RequestExit()`), and `history.Current` always names the top-most destination on
`_screens`, with modals transparent to it. The feed's ambiguous case (#403 case 2) is resolved by
treating it as a modal: a task opened *from* the feed is **not** pushed onto history, reusing the
existing quick-open "requester" skip.

## Two categories

- **Destination** — a genuine back location in the task chain, recorded in `NavigationHistory<T>`.
  The root is per-launch-mode (the list in `TodoApp`; the launch task in `SingleTaskApp` — see
  `docs/plans/browser-style-navigation-history.md`). Only **Task Detail** qualifies.
- **Transient modal** — an overlay that dismisses to whatever is beneath it. Rides the `_screens`
  LIFO view-stack (`TodoApp.ShowScreen`/`CloseScreen`) only; **never** touches `NavigationHistory`.
  A browser doesn't put a dialog in its history, and neither do we.

## The taxonomy (every screen)

| Screen | Trigger | Category | `Esc` behavior | In `NavigationHistory`? |
|---|---|---|---|---|
| `TaskDetailScreen` | Enter on list; detail→detail (`OpenTaskRequested`); Ctrl+O resolve; feed row | **Destination** | Back — walk history one task | **Yes** — pushed at the single `OpenTaskDetail` site |
| `HelpScreen` (F1) | over any screen | Modal | Dismiss to layer beneath | No |
| `SettingsScreen` (F2) | list | Modal | Cancel (discard edit) | No |
| `FilterSortGroupScreen` (F3) | list | Modal | Cancel (`Result` null) | No |
| `QuickUpdatesScreen` (Ctrl+U) | list / over detail | Modal | Cancel | No |
| `NewTaskScreen` (Ctrl+N) | list | Modal | Cancel | No |
| `QuickOpenScreen` (Ctrl+O) | list / over detail | Modal (entry surface) | Cancel — the resolved task is the destination, not the surface | No (the task it resolves is pushed) |
| `AgentRunScreen` | over detail | Modal | 1st Esc cancels the in-flight run; when finished, Esc closes | No |
| `PromptTemplateEditorScreen` | over Settings | Modal (nested) | Cancel | No |
| `NotificationsFeedScreen` (Ctrl+E) | list ↔ feed | **Modal** (see "The feed", below) | Back to list | **No** |

## The `Esc` / `RequestExit` contract

One chokepoint, evaluated top-down:

1. **A transient modal is on top of `_screens`** → `Esc` closes that modal (`Screen.Close()`),
   restoring the layer beneath. `NavigationHistory` is untouched. (Covers #403 case 1 — overlays
   over a detail such as Ctrl+U, F1, and the Ctrl+O surface must not push/pop the task history.)
2. **A destination (detail) is on top** → `Esc` calls `history.TryBack(out prev)`:
   - returns `true` → pop the detail from `_screens` and show `prev` (the previous detail, or the
     list root). The view-stack and the history move in lockstep.
   - returns `false` (at root) → hand off to `RequestExit()`. This only happens in `SingleTaskApp`,
     whose root *is* a detail.
3. **At the host root with nothing stacked** → `Esc`/quit → `RequestExit()` (the
   [#299](https://github.com/rbcministries/clickup-todo-cli/issues/299) exit-confirmation plug
   point). `TodoApp` root = list; `SingleTaskApp` root = launch task.

**Invariant.** `history.Current` always names the top-most **destination** on `_screens`, ignoring
any modals layered above it. Modals are transparent to history — that is the entire point of the
split, and it is what lets #403 keep the history a faithful mirror of the destination back-path
without teaching it about every overlay.

## The feed (resolving #403 case 2)

Opening a task from the feed makes the view-stack `[list, feed, detail]` while a list-rooted task
history is `[list, task]` — the two diverge on `Esc` (the view restores the feed; a naive history
pop would go to the list). **Decision: the feed is transparent to history (feed-as-modal).**

- Classify `NotificationsFeedScreen` as a transient modal.
- A task opened *from* the feed is **not** pushed onto `NavigationHistory`. Reuse the exact
  mechanism quick-open already uses to skip its own mount: `OpenTaskDetail` captures the modal as
  its "requester" and skips the history push when the open originates from a modal surface (see the
  quick-open "requester" note in `TodoApp.ShowQuickOpenSurface`).
- Result: history stays `[list]`; the `_screens` stack alone drives the feed→detail sub-flow, and
  `Esc` from a feed-opened detail restores the feed (correct UX) with history untouched at the root.

Rationale: this is the smaller blast radius, matches the "tasks-only history" framing, and the
requester-skip plumbing already exists. The accepted cost is that detail→detail navigation started
*inside* a feed launch is not recorded in the shared history.

**Alternative, deferred:** make the feed a first-class nav target (`T` is already a nav-target
union — list *or* task — so a feed variant fits), giving `[list, feed, task]` that mirrors
`_screens` faithfully and walks in lockstep under a future Forward key. Revisit only if/when a
Forward key or breadcrumb actually consumes the history and the feed needs to be a reachable
"forward" step.

## Consequences

- **#403** wires one `NavigationHistory` per host, pushed at the single `OpenTaskDetail` site,
  covering the tree and Ctrl+O paths; the feed path is guarded by the requester-skip so it does not
  push. The reconciliation for the three #403 design questions is fully specified above (case 1 =
  contract rule 1; case 2 = the feed decision; case 3 = `SingleTaskApp` root under contract rules 2
  and 3).
- **#346**'s shared `ScreenStackHost` wraps the `_screens` seam once #403 lands, and needs to model
  only the destination back-stack plus modal overlay/dismiss — not two competing history mechanisms.
- **No behavioural regression** is expected: `Esc` = Back already walks `_screens` one screen at a
  time today; this ADR makes `NavigationHistory` the logical mirror of that walk rather than changing
  it. Verified via the existing `tui-validate` detail/quick-open/tree/feed scenarios (CLAUDE.md).
- **Native modals** for the transient-modal category are an open exploration
  ([#404](https://github.com/rbcministries/clickup-todo-cli/issues/404)); this ADR classifies those
  surfaces as modals regardless of whether they stay on the custom `_screens` host or migrate to
  Terminal.Gui's native modals.
