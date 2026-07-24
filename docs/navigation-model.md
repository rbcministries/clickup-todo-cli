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
recording the task opened *from* the feed as a normal **list-rooted** history entry (the feed screen
itself is not an entry): back-navigation from a feed-opened task — and any detail→detail trail beyond
it — walks back to the main list, bypassing the feed. This intentionally changes today's #115
"Esc returns to the feed" behaviour.

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
| `ExitConfirmScreen` (#299) | `RequestExit()` — contract rule 3 (and rule 2 at `SingleTaskApp`'s root) | Modal | Cancel — dismiss to the root view beneath | No |

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
3. **At the host root with nothing stacked** → `Esc`/quit → `RequestExit()`. `TodoApp` root =
   list; `SingleTaskApp` root = launch task. **As of
   [#299](https://github.com/rbcministries/clickup-todo-cli/issues/299) this plug point is
   filled:** `RequestExit()` mounts the `ExitConfirmScreen` modal (`Y`/`Enter` exits, `N`/`Esc`
   dismisses back to the root) instead of stopping the app directly. The confirm screen is itself
   a transient modal under rule 1 — it is never a history entry, and because it consumes every key,
   nothing but an explicit `Y`/`Enter` can leave the app while it is up (`Esc` there is the "no"
   answer, `Ctrl+Q` is ignored).

**Invariant.** `history.Current` always names the top-most **destination** on `_screens`, ignoring
any modals layered above it. Modals are transparent to history — that is the entire point of the
split, and it is what lets #403 keep the history a faithful mirror of the destination back-path
without teaching it about every overlay.

## The feed (resolving #403 case 2)

Opening a task from the feed makes the view-stack `[list, feed, detail]` while the list-rooted task
history is `[list, task]`. **Decision: the task opened from the feed is a first-class, list-rooted
history entry; the feed screen itself is not.**

- The feed screen (Ctrl+E) is a transient modal — not a history entry.
- A task opened *from* the feed is pushed onto the single list-rooted `NavigationHistory` at the
  normal `OpenTaskDetail` site, exactly like a task opened from the list. **No feed special-casing,
  no requester-skip** — the feed uses the same push-site as every other task-detail open.
- Back-navigation therefore runs `task_n → … → task1 → list`: the first (feed-opened) detail goes
  back to the **main list**, and any detail→detail trail opened beyond it walks back through those
  tasks to that first task, then to the list.
- The feed is a **launcher, not a back-stop.** Because destinations are rooted at the list and
  transient modals never sit on the destination back-stack, the feed modal is left behind when you
  navigate into the task chain. Whether #403 dismisses the feed eagerly (on open) or tears it down
  when back-navigation passes the first task is an implementation choice; the observable contract is
  only that back from the feed-opened task lands on the **list**.

**Behavioural change to flag — not a silent regression.** This intentionally changes today's #115
behaviour, where `Esc` from a feed-opened detail returns to the *feed*. Under this model that `Esc`
returns to the *list*, and the feed is reached again via Ctrl+E. The feed E2E scenario (currently
asserting "Esc returns here") must be updated to assert the list; #403 should treat that as an
intended contract change, not a regression against #402's no-regression criterion.

**Why this over the earlier "feed-as-modal-with-skip" proposal.** Recording the feed-opened task
keeps one coherent back-trail from feed-launched deep navigation all the way to the list, and makes
the taxonomy table literally true (the `TaskDetailScreen` row already lists "feed row" as a push
trigger). The earlier skip left feed-launched detail→detail navigation outside history entirely — a
gap this closes.

**Rejected alternatives:**
- *Feed-as-modal with requester-skip* (the earlier proposal): the feed-opened task is not recorded;
  `Esc` returns to the feed via the view-stack. Rejected — leaves feed-launched navigation out of
  history.
- *Feed as a first-class nav target* (`[list, feed, task]`): records the task **and** returns to the
  feed on back. Rejected per maintainer preference — back from a feed-opened task should go to the
  list, bypassing the feed. Revisit only if return-to-feed is later wanted.

## Consequences

- **#403** wires one `NavigationHistory` per host, pushed at the single `OpenTaskDetail` site,
  covering the tree, Ctrl+O, **and feed** paths — the feed uses the same push-site with no skip. The
  reconciliation for the three #403 design questions is fully specified above (case 1 = contract rule
  1; case 2 = the feed decision; case 3 = `SingleTaskApp` root under contract rules 2 and 3).
- **#346**'s shared `ScreenStackHost` wraps the `_screens` seam once #403 lands, and needs to model
  only the destination back-stack plus modal overlay/dismiss — not two competing history mechanisms.
- **One deliberate behavioural change, otherwise no regression.** `Esc` = Back already walks
  `_screens` one screen at a time today; this ADR makes `NavigationHistory` the logical mirror of
  that walk. The single intended change is the feed back-target (`Esc` from a feed-opened detail now
  lands on the list, not the feed — see above), which requires updating the feed `tui-validate`
  scenario. The detail / quick-open / tree scenarios stay green unchanged.
- **Native modals** for the transient-modal category are an open exploration
  ([#404](https://github.com/rbcministries/clickup-todo-cli/issues/404)); this ADR classifies those
  surfaces as modals regardless of whether they stay on the custom `_screens` host or migrate to
  Terminal.Gui's native modals.
