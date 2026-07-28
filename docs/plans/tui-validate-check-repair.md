# Plan — #409: repair two `tui-validate` checks that can't be run as written

> **Goal (from the issue).** Two PTY-harness checks fail on a clean `main` when run as
> documented — `qu_click_check.py` ("could not open Quick Updates on pt1") and
> `double_click_check.py` ("double-click on empty space wrongly opened detail"). A check that
> always fails is indistinguishable from a check nobody runs, so the mouse click-to-apply path
> (#288) and the double-click open path (#286) are effectively unguarded. Make both pass exactly
> as documented, with **no operator-supplied env**.

Both are **check-script rot**, not app regressions — the app behaves correctly; the checks drifted
away from it. No C# changes.

## Root causes (verified by reproducing on `main`)

1. **`qu_click_check.py` — Quick Updates was rebound Space → Ctrl+U (#159/#290).** The check opens
   Quick Updates with Space (`b" "`); since #290 standardized the action to **Ctrl+U**
   (`Keybindings.cs` → `(MainList, QuickUpdate) = "Ctrl+U"`; footer reads "Ctrl+U quick update"),
   Space no longer opens it, so Phase A's open loop exhausts its 8 tries and asserts. Reproduced:
   `Assertion: could not open Quick Updates on pt1`.

2. **`qu_click_check.py` — Phase B leaks an ambient `E2E_FOREIGN`.** The docstring's "Phase A
   (E2E_FOREIGN=1 …)" note invites an operator to export `E2E_FOREIGN=1` (as the issue reporter
   did). `boot()` builds its env from `os.environ`, so that ambient value leaks into **Phase B**,
   which needs the *default* Assignee-PUT backend — swapping the backend out and breaking the
   assignee add/remove leg. (Latent: Phase A aborts first when run the reporter's way, so this only
   surfaced once the Space→Ctrl+U fix let Phase A pass.)

3. **`double_click_check.py` — the "empty space" row assumes a 20-task list but inherits the
   200-task default.** `EMPTY_ROW = 20` matches the check's own comment ("short list — 20 tasks"),
   but its `env` dict sets only `TERM`, so at the harness default (`E2E_TASKS=200`) screen row 20 is
   a real task row; the empty-space double-click then opens a detail and the check fails with a
   message that reads like an app regression.

## Fixes

- **`qu_click_check.py`:** open Quick Updates with `CTRL_U = b"\x15"` in both phases (matching the
  `Ctrl+E=\x05` / `Ctrl+N=\x0e` convention the sibling checks already use). Boot Phase B with
  `E2E_FOREIGN="0"` so an ambient `E2E_FOREIGN=1` can't leak in (the harness treats anything ≠ `"1"`
  as off). Refresh the docstring: each phase sets the backend knob it needs, so the check needs no
  operator env.
- **`double_click_check.py`:** set `E2E_TASKS="20"` in the check's own `env` dict, pinning the short
  list its `EMPTY_ROW=20` assumes instead of inheriting the default — the one-line fix the issue
  suggests, matching how the sibling checks pin their own task count rather than inherit it
  (`tree_tab_check.py` `E2E_TASKS=6`, `fold_click_check.py` `E2E_TASKS=8`, `exit_confirm_check.py`
  `E2E_TASKS=20`; `tab_boundary_check.py` sets its own `E2E_TREE=1`).

## Not changed

- **No skill inventory change.** `SKILL.md` lists **mouse input** under "Not covered (still needs a
  human pass)"; the mouse checks are the ad-hoc human-pass tooling (run when a PR touches mouse), not
  part of the routine numbered checks. Repairing them keeps them usable for that pass without
  changing the skill's curated automated set.
- **`qu_click_check.py` is repaired, not retired** (AC option 2), so #288's click-to-apply path is
  guarded again.

## Verification

- `qu_click_check.py` **PASS** both ways: with no env, and with a hostile ambient `E2E_FOREIGN=1`
  (previously broke Phase B) — proving the leak is closed.
- `double_click_check.py` **PASS** run exactly as documented (no operator env).
- `dotnet build -c Release` 0/0, `dotnet test -c Release` green, `dotnet format --verify-no-changes`
  clean (Python-only changes; the C# tree is untouched).
