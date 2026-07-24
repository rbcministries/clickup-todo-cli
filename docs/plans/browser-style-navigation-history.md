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

### 2. `SingleTaskApp` — full, self-contained integration

Single-task mode's navigable surface today is `detail (root) ↔ Help`. Adopt the model as the
one back-stack:

- Seed the history with the **launch task** detail as root.
- `Alt+←` / `Esc` = back (pop the overlay); `Alt+→` = forward (re-open the overlay just backed
  out of); at the root, back/`Esc` → `RequestExit()`.
- `RequestExit()` is the **#299 seam**: today it does what Esc-at-root does now
  (`Application.RequestStop()` — quit the tab), with the modal to layer on in #299.

### 3. `TodoApp` — exit seam + unified Esc/Alt+← at the list root (minimal, safe)

The dashboard's `_screens` stack (list root + Settings/Detail/Feed/QuickUpdates/Help) is
load-bearing behind the single-sectioned-`ListView` invariant (#3/#38), so this PR touches it
minimally and additively:

- Add `RequestExit()` — the same **#299 seam** — and route the list-root `Esc` (and the new
  `Alt+←`) through it instead of calling `Application.RequestStop()` inline. `Alt+←` on an open
  screen is a **back** alias for `Esc` (close the top screen).
- Bind `Alt+→` (forward). With no forward target it flashes an explanatory hint.
- Add the `Alt+←/→ back/forward` footer hints where they apply.

## Deferred (out of scope, tracked)

- **Dashboard detail→detail multi-entry history + `tui-validate` "navigate detail→detail and
  back".** The dashboard has no detail→detail navigation yet — that is
  [#291](https://github.com/rbcministries/clickup-todo-cli/issues/291) (Task Tree, open in
  PR #373), which the issue says *drives* this history. This PR lands the mechanism + the root
  and exit seams; the dashboard's full adoption of forward/back across visited **tasks** lands
  with #291 so the two don't conflict on the same `_screens` wiring. Noted for land-ordering in
  the PR.
- **Exit-confirmation modal.** `RequestExit()` is the plug point; the modal itself is
  [#299](https://github.com/rbcministries/clickup-todo-cli/issues/299) (sub-issue 7). Until it
  lands, `RequestExit()` preserves today's exit behavior.

## Tests

- **Unit (`NavigationHistoryTests`):** push/back/forward; forward truncation on a fresh push;
  `TryBack` false at root; root never evicted under the cap; `CanGoBack`/`CanGoForward`/`AtRoot`
  transitions — parameterized over a "list" root and a "launch task" root ("both modes").
- **TUI (not CI-unit-testable):** verified via build + reasoning and a `tui-validate` scenario
  that boots single-task mode (`--task <id>`), opens Help (F1), and asserts `Alt+←` / `Esc`
  returns to the detail and `Esc` at the detail root quits. Dashboard detail→detail validation
  rides #291.
