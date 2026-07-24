# Browser-style navigation history — Alt+←/→, Esc = back, per-launch-mode root

Issue: [#298](https://github.com/rbcministries/clickup-todo-cli/issues/298) (Multi-tab
sub-issue 6, epic [#292](https://github.com/rbcministries/clickup-todo-cli/issues/298)).
Depends on sub-issue (4) [#296](https://github.com/rbcministries/clickup-todo-cli/issues/296)
(single-task launch mode) — **merged**, so the per-launch-mode "launch task" root concept
this builds on exists.

## Goal

Replace the shallow, one-deep screen handling with a real **back/forward navigation
history**: `Alt+←` / `Alt+→` move through visited screens/tasks, `Esc` = back except at the
**root**, and the root differs by launch mode (the **main list** in the dashboard; the
**launch task** in single-task mode). History never navigates above the root; at the root,
back/quit hands off to an **exit seam** where the exit-confirmation modal
([#299](https://github.com/rbcministries/clickup-todo-cli/issues/299), sub-issue 7) will
later plug in.

## Why this shape

- The issue frames the deliverable as *the history mechanism* that other work drives:
  "#291's Task Tree navigation drives this history (no separate back-stack)." So the core,
  reusable artifact is a **pure `NavigationHistory<T>` model** — testable without a terminal,
  exactly like the repo's other pure glue (`DetailTabNav`, `DetailScrollModel`,
  `DispatchPaneModel`, `HelpLine`). The two hosts and the future detail→detail navigation all
  consume the same model, so there is one back-stack, not several.
- **Root is a construction parameter, not a special case.** The model is seeded with a root
  entry (index 0) that is immutable and never evicted. `AtRoot` ⇔ `index == 0`; back at the
  root returns `false` and the host routes that to the exit seam. Seeding the dashboard with a
  "list" root and single-task mode with a "launch task" root is all that "per-launch-mode root"
  requires — no mode flag inside the model.
- **Browser semantics, resolved from the issue's open questions:**
  - *Forward truncates on a fresh push* (the issue's stated default). Visiting a new
    screen after going back discards the forward entries, like every browser.
  - *History depth is bounded.* A `MaxDepth` cap (default **50**) bounds growth; when a push
    would exceed it, the **oldest non-root** entry is evicted (the root is pinned so the root
    invariant holds). 50 is far beyond any realistic hand-navigation depth while still bounding
    a pathological link-following loop.

## Scope (this PR)

### 1. `Tui/NavigationHistory<T>` — the pure model (primary deliverable)

A generic, terminal-free back/forward stack:

- Constructed with a **root** entry and an optional `maxDepth` (default 50).
- `Current`, `AtRoot`, `CanGoBack`, `CanGoForward`, `Count`, plus `Entries`/`Index` for test
  introspection.
- `Push(entry)` — truncates any forward entries, appends, advances `Current`, and enforces the
  cap by evicting the oldest non-root entry.
- `TryBack(out entry)` / `TryForward(out entry)` — move `Current` and report the entry to show;
  `TryBack` returns `false` at the root (the caller hands off to the exit seam).

Fully unit-tested (`NavigationHistoryTests`) for **both modes** by parameterizing the root
entry: push/back/forward round-trips, forward-truncation-on-push, root-never-escaped,
back-at-root, and cap eviction (root pinned).

### 2. `RequestExit()` — the exit-confirmation seam, both hosts

Both hosts now funnel every "quit from a root view" through a single `RequestExit()` chokepoint
(the #299 plug point; also resolves the #290 Esc-drift by naming one exit path):

- **`SingleTaskApp`**: the launch-task detail is the root; its `Esc`/close hands off to
  `RequestExit()` (Ctrl+B still opens the browser then quits).
- **`TodoApp`**: the list root's `KeyAction.Quit` binding, `Esc`, and Ctrl+C all route through
  `RequestExit()` instead of calling `Application.RequestStop()` directly.

Today `RequestExit()` preserves the existing behavior (stop the app); #299's confirmation modal
plugs in here.

## Key-chord decision (pending)

The originally-planned back/forward chord — **Alt+←/→** — collides with terminal-emulator split-
pane navigation (e.g. Windows Terminal binds Alt+arrows to move focus between panes). The chord
is therefore **being re-chosen with the maintainer** (alternatives posted on #298) before the
back/forward key binding + host adoption land. `Esc` = back-at-root → exit is unaffected and is
wired now.

## Deferred (out of scope, tracked)

- **Back/forward key binding + host adoption.** Held until the chord is decided (see above). The
  dashboard's back/forward across visited **tasks** additionally needs the detail→detail
  navigation from [#291](https://github.com/rbcministries/clickup-todo-cli/issues/291) (Task
  Tree, open in PR #373), which the issue says *drives* this history — so it lands with #291,
  which plugs into the shared `NavigationHistory` this PR delivers rather than rolling its own.
- **Exit-confirmation modal.** `RequestExit()` is the plug point; the modal itself is
  [#299](https://github.com/rbcministries/clickup-todo-cli/issues/299) (sub-issue 7). Until it
  lands, `RequestExit()` preserves today's exit behavior.

## Tests

- **Unit (`NavigationHistoryTests`):** push/back/forward; forward truncation on a fresh push
  (single- and multi-entry); `TryBack` false at root; root never evicted under the cap;
  exact-at-cap retains everything; `CanGoBack`/`CanGoForward`/`AtRoot` transitions — parameterized
  over a "list" root and a "launch task" root ("both modes").
- **TUI (not CI-unit-testable):** `RequestExit()` behavior is covered by the existing
  `single_task_launch_check.py` (Esc at the launch-task root quits). The back/forward key
  scenario is added once the chord is chosen.
