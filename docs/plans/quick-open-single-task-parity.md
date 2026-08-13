# Quick-open (C): Ctrl+O parity in single-task launch mode — #616

Slice **C** of the Ctrl+O quick-open epic (#613). `Ctrl+O` is dashboard-only
today: `TaskDetailScreen` raises `QuickOpenRequested` from any host, but only
`TodoApp` subscribes — `SingleTaskApp` never does, so pressing `Ctrl+O` in
single-task launch mode (`--task <id>`) does nothing, silently, even though the
detail footer already advertises the `🗁 by ID` key. This slice makes the
already-advertised key do something in that host.

Depends on **B** (#615, merged as PR #622), which put the launch-mode **intent**
on the surface's result (`QuickOpenRequest { Text, QuickOpenIntent }` with
`OpenHere | NewTab | SplitPane`). This slice consumes that intent in the
single-task host.

## The recorded design decision — what `OpenHere` means here

`SingleTaskApp`'s root **is** a task detail, so "open in place" needs a
definition. Two coherent readings (per the issue): navigate the existing detail
to the resolved task, or treat in-place as a new tab.

**Decision: navigate — stack the resolved task's detail over the current one on
this host's back-stack (`_stack`), so a single `Esc` walks back to the launch
task.** This matches the host's *existing* detail→detail navigation exactly: a
Task Tree row (#374) and a clicked task link (#318) both already open through
`OpenTaskDetail`, which stacks a child over the current detail; the launch task
stays the root, and `Esc` at that root still hands off to the exit confirmation
(#298/#299). That is the single-task host's realisation of the #402 navigation
contract (rule 2: `Esc` = Back walks the visited-task chain). Treating in-place
as a new tab would fork a second navigation model into a host that already has
one, for no gain — rejected.

So `OpenHere` reuses the host's `OpenTaskDetail` / custom-id resolve path
verbatim; there is no new navigation machinery.

## Scope (host wiring only — no new pure logic)

Everything pure this slice needs already ships and is unit-tested:
`QuickOpenParser.Parse` / `FindInCache` / `ResolveLaunch` and
`QuickOpenScreen` / `QuickOpenRequest.From` (B, #615). This slice is the
`SingleTaskApp` subscription + intent routing:

1. **Subscribe `QuickOpenRequested`** in `BuildDetailTab` (every detail tab —
   root and any child opened by walking the tree), mounting a `QuickOpenScreen`
   through the host's own `ShowScreen`.
2. **Defer the resolve one main-loop iteration** with `Application.AddTimeout(1ms)`
   after the surface closes — mirroring `TodoApp.ShowQuickOpenSurface`. This is
   load-bearing, not cosmetic: `ShowScreen`'s close handler runs `onClosed`
   *before* it tears the surface down, so resolving inline would capture the
   still-mounted quick-open screen as the open's "requester" and then drop the
   open once the surface closes (the exact bug the dashboard hit under
   `tui-validate`). The deferral covers all three intents so there is one path.
3. **Route the intent:**
   - `OpenHere` → `ResolveAndOpen(text)`: `QuickOpenParser.Parse` then the
     host's existing terminus — a plain id (or hyphenless custom id) through
     `OpenTaskDetail(value, customIdFallbackTeamId: teamId)`; a `/t/{team}/{custom}`
     or bare custom id through `ResolveCustomIdAndOpen(value, teamId)`; a custom
     id with no configured workspace, and an unparseable token, each flash the
     same messages the dashboard uses. `teamId` prefers a URL's own team id over
     the configured workspace (so a link pasted from another workspace resolves
     against that one) — identical to `ActivateLink` (#353).
   - `NewTab` / `SplitPane` → `ResolveAndLaunch(text, location)`:
     `QuickOpenParser.ResolveLaunch(universe: [], text)` then the host's shared
     `LaunchAppForTask(id, name, location)`. Single-task mode holds **no working
     set**, so the universe is empty and `ResolveLaunch` always takes its
     miss-branch — handing the raw trimmed token to the child, whose `--task`
     resolves every Ctrl+O reference form (#464). An unparseable token flashes
     and launches nothing. `LaunchAppForTask` already brings the in-flight guard,
     the #505/#515 split-viability floor, the split→tab→window ladder and the
     clipboard fallback.

No footer / keybinding / `HelpLine` change: the detail screen dispatches `Ctrl+O`
itself (a hardcoded chord check that raises `QuickOpenRequested`) and advertises
`🗁 by ID` from the host-independent `HelpItemSets`, so the single-task footer
already shows it — this slice only makes the advertised key live.

## Tests

- **Unit** — pin the one contract this host newly relies on:
  `QuickOpenParser.ResolveLaunch` over an **empty** universe (single-task mode's
  no-cache case) returns the raw trimmed token as both id and display name for a
  plain id, a bare custom id and a `/t/{team}/{custom}` URL, and `null` for an
  unparseable token. (The dashboard's cache-hit path is already covered; this
  documents the miss-only path C always takes.)
- **`tui-validate`** — a new `single_task_quick_open_check.py`: from a
  `--task` launch, `Ctrl+O` opens the entry surface; typing another task's id
  and `Enter` (OpenHere) navigates the detail to it; `Esc` walks back to the
  launch task (the recorded decision). Plus the driver-robust launch legs where
  reachable: a `Ctrl+Enter` / the `New tab` button under the headless harness
  degrades to the copy-command fallback flash (no emulator to target), asserting
  the gesture reached the launch path rather than being a dead key. Run only
  after `dotnet test` is green (CLAUDE.md).

## Hard-rules check

- No `Generated/` hand-edits; no spec change / Kiota regen — pure host wiring.
- ClickUp auth quirk untouched.
- **No second focusable pane / no latency regression (#3):** the quick-open
  surface is a modal `Screen` mounted on the existing `_stack` (one visible
  layer at a time via `ShowScreen`), exactly as Help / the exit confirm / a
  stacked child detail already are — not a second persistent focusable pane.
- Bare-letter type-ahead (#12) intact — `Ctrl+O` and the launch chords are named
  keys.
- Tests land with the code; none weakened; no integration test needed (no new
  ClickUp boundary — the launch hands a token to a child process).

## Deferred / non-goals

- The Feed host (`FeedApp` has no quick-open surface — a feature, not parity),
  per the epic.
- The native-modal re-host of the surface (slice **E**, #618).
