# Design plans

Short design notes for non-trivial features — the "plan" half of the
plan-then-issue workflow that `/implement-issue` follows. Plans live here (not
under `.claude/`) so scheduled/unattended sessions can write them without a
permission prompt for the `.claude/` tree.

## Layout

- **`docs/plans/`** (this directory) — **active** plans for work that is in
  flight or not yet merged. New plans start here as `docs/plans/<feature>.md`.
- **`docs/plans/completed/`** — plans whose work has shipped (PR merged). Moving
  a finished plan here keeps the active listing short so a fresh session isn't
  wading through a hundred done notes to find the one that matters.

## Lifecycle

1. **Write** a plan for a non-trivial feature at `docs/plans/<feature>.md`,
   grounded in the issue's acceptance criteria.
2. **Implement** against it (see `.claude/commands/implement-issue.md`).
3. **Retire** it when the PR merges: `git mv docs/plans/<feature>.md
   docs/plans/completed/`.

Completed plans are kept indefinitely as provenance for the decisions behind
shipped code — there is no automatic expiry. If the `completed/` listing ever
grows unwieldy, prune by hand; git history preserves anything removed.
