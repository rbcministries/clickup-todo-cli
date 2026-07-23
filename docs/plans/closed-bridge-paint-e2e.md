# Plan: tui-validate scenario for the instant closed-task bridge paint (#333)

Split out from #280 (Part 2), deferred from #253. #253 shipped the warm, bounded
closed-task cache that bridge-paints the F12→All transition; #280 Part 1 (#334) made
that set persist across restarts. Neither shipped an **end-to-end** assertion that the
bridge paint _itself_ — F12→All showing the warm closed set **before** the authoritative
`include_closed=true` refresh lands — actually happens. This issue adds that scenario to
the `tui-validate` PTY harness.

## Why it needs harness plumbing (not just a scenario file)

Two properties of the harness make the pre-refresh frame invisible today:

1. **The warm set is always empty during a run.** The E2E host builds `TaskService`
   **without** a state store (so no cross-restart warm, #334) and runs with
   `E2E_REFRESH=600`, so the background closed-prefetch cadence never fires inside a
   short test. `SupplementWithClosed` is therefore always a no-op → the bridge paints
   nothing.
2. **The authoritative refresh returns the closed task instantly.** The fake backend
   serves `tclosed` on any `include_closed=true` fetch, in-process with no latency. So
   after F12→All the closed row appears _regardless_ of the bridge — there is no window
   in which the bridge is the only thing that could have painted it.

A third, subtler trap: the warm cache applies a **30-day age window** on
`date_updated`, and the fake's `tclosed` is dated `1751500000000` (≈2025-07-02) — over a
year before the current date — so even if we warmed the cache it would be pruned as
stale. The seed must carry a **recent** `date_updated`.

## Approach — two small, default-off harness seams + an A/B check

All new behaviour is gated behind env flags that default off, so every existing scenario
keeps its byte-identical cold-first-paint A/B parity (the harness's core guarantee).

### Seam 1 — warm-now hook (`E2E_WARM_CLOSED=1`)

In `tests/ClickUpTodo.Tui.E2E/Program.cs`, when the flag is set, `await
tasks.PrefetchClosedTasksAsync()` **before** `new TodoApp(...).Run(...)`. This exercises
the real fetch→map→`ClosedTaskCache.Update` path (not a synthetic inject), populating the
in-memory warm set from the fake backend before the TUI boots. No state store needed —
`Update` sets the in-memory snapshot regardless of persistence.

The same flag makes the fake serve the closed task with a **recent** `date_updated`
(`now − 1 day`) so it survives the 30-day age window. Gated behind the flag so the
existing `tclosed` date (which the feed checks rely on for comment sort order) is
untouched when the flag is off.

### Seam 2 — refresh-stall hook (`E2E_STALL_CLOSED_MS=<ms>`)

The fake backend delays the response to `include_closed=true` **team-task** fetches by
the given milliseconds, but only once "armed". `Program.cs` arms the stall (a static flag
on the fake) **immediately before `Run()`** — i.e. after any warm prefetch has already
completed. Result:

- The pre-`Run` warm prefetch's `include_closed` fetch is **never** stalled (unarmed).
- The post-boot F12→All authoritative refresh **is** stalled, opening a deterministic
  window in which the screen holds the pre-refresh frame.

Initial boot loads `include_closed=false` (default `Active` view), so it is never stalled.

### The check — `tests/ClickUpTodo.Tui.E2E/closed_bridge_check.py`

Two harness launches (one script, one command), an A/B that isolates the bridge:

- **Warm leg** (`E2E_WARM_CLOSED=1 E2E_STALL_CLOSED_MS≈2500`): boot, `F12`×2 → All,
  capture the frame _during_ the stall window → assert the closed row
  ("Closed ticket — shipped and done") is **present** (only the bridge could have painted
  it — the authoritative refresh is still blocked). Then wait past the stall → assert it
  is **still** present (authoritative superset converged, no flicker-out).
- **Control leg** (`E2E_STALL_CLOSED_MS≈2500`, no warm): boot, `F12`×2 → All, capture
  during the stall → assert the closed row is **absent** (empty warm set → bridge is a
  no-op), then wait past the stall → assert it **appears** (authoritative path still
  works). This proves the warm leg's early row is the bridge, not the refresh.

Timing is robust, not raced: the stall (~2.5 s) dwarfs the pyte read cadence (ms-scale),
so the "during stall" capture lands deterministically inside the window and the "after
stall" capture deterministically outside it.

## Files

- `tests/ClickUpTodo.Tui.E2E/Program.cs` — warm-now hook, recent-date seed, arm the stall
  before `Run()`; the fake gains the counted/armed `include_closed` stall.
- `tests/ClickUpTodo.Tui.E2E/closed_bridge_check.py` — the A/B check (new).
- `.claude/skills/tui-validate/SKILL.md` — document the new scenario + env knobs.

## Invariants

- **No product-code change.** Only the E2E harness (`tests/`), the check script, and the
  skill doc. `SupplementWithClosed` / `WarmClosedTasks` / `PrefetchClosedTasksAsync` are
  used exactly as the app uses them.
- **Generated client / curated spec untouched** (no API surface change — `include_closed`
  already exists).
- **A/B parity preserved:** every new flag defaults off, so the existing checks
  (`color_check`, `detail_check`, `screen_check`, …) see the identical cold-first-paint
  behaviour and stay byte-identical to the stock renderer.
- No second focusable pane (#3); no new keybinding (F12 already owns the cycle).

## Out of scope

- Asserting the truncation note ("Older completed omitted until refresh.") — the count
  cap isn't exercised by a single-task seed; the note path is already unit-covered.
- Persisted-warm-set startup path (#334) in E2E — the harness intentionally runs without
  a state store to keep cold-paint parity; the warm-now hook covers the same code path.
