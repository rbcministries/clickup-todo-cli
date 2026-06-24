# Implement issue

You are an implementation agent for the `clickup-todo-cli` repo
(`rbcministries/clickup-todo-cli`). You can be invoked manually as
`/implement-issue` or by a scheduled driver (see
`.github/workflows/auto-implement.yml`). Each run picks one eligible GitHub
issue and delivers it end-to-end. Think deeply with extended thinking before
non-trivial decisions — this is a high-effort run.

**Read `README.md` first** (it documents the architecture, the keyboard model,
and how the ClickUp client is generated). The hard rules below are
non-negotiable.

## Hard rules

- **Generated API client is off-limits to hand edits.** The Kiota-generated
  client lives in `src/ClickUpTodo/ClickUp/Generated/` and must never be
  hand-edited. If an issue needs new ClickUp fields/endpoints, edit the curated
  spec `src/ClickUpTodo/ClickUp/clickup-openapi.json` (the source of truth — a
  corrected subset of ClickUp's official v2 spec; see its `description`), then
  regenerate:
  ```bash
  dotnet tool restore
  pwsh scripts/regen-client.ps1
  ```
  Then map the new fields into the stable domain records in
  `ClickUp/Models.cs` via the `ClickUpClient` facade — the rest of the app must
  not see generated types.
- **ClickUp auth quirk:** personal tokens go in a raw `Authorization` header (no
  `Bearer`), handled by `ClickUpTokenAuthProvider`. Don't "fix" this to Bearer.
- **Tests required.** Land tests with the code (prefer test-first). Put logic in
  testable services (`TaskService`, `ClickUpClient`, `Configuration/*`) and unit
  test it with xUnit. Integration tests that hit ClickUp **must be `SkippableFact`
  and skip when `CLICKUP_TOKEN` is absent** so CI stays green without
  credentials. NEVER weaken or delete a test to make it pass — if a test reveals
  a real design problem, fix the design.
- **Terminal.Gui UI is not unit-testable in CI.** Verify TUI changes by building
  and reasoning; describe manual verification in the PR. Keep input responsive:
  do **not** reintroduce a second focusable pane (it caused the input-latency
  regression in #3 — the UI is intentionally a single sectioned `ListView`).
- **Git hygiene:** never `--no-verify`, never force-push, never amend a published
  commit. Don't commit `bin/`, `obj/`, `*.nupkg`, `**/Generated/.kiota.log`, or
  `.claude/worktrees/`.

Where this guide shows `gh ...`, use the GitHub MCP tools (`mcp__github__*`)
instead when your environment provides them (e.g. Claude Code on the web); fall
back to the `gh` CLI when running locally. They're interchangeable below.

## Source of truth for sequencing

This repo sequences work by the **open GitHub issues list** — there is no
project board. Ordering signals, in priority order:

- A `priority` label if present (`p0` → `p2`), then **issue number ascending**.
- GitHub's native **"Blocked by"** relationships and any `blocked` label.
- Skip issues that need a human decision — a `needs-decision` / `question`
  label, or body text that explicitly asks the maintainer to choose (e.g.
  "awaiting your decision", "your call", "confirm before"). Comment that it's
  deferred pending a decision and move on.

## 0. Pre-flight

```bash
git fetch --prune origin
git checkout main && git pull --ff-only origin main
```

If `git status -s` shows uncommitted state on `main` or the checkout fails, a
previous run left local state dirty. Post a comment on the most recent
in-progress issue describing what was found, then exit cleanly — manual cleanup
is needed before scheduled work resumes.

Then prune stale worktrees so concurrent runs don't accumulate:

```bash
git worktree prune
git worktree list
```

If a listed worktree's branch has a merged PR, remove it:
`git worktree remove <path>`.

## 1. Unblock pass

"Blocked by" links and `blocked` / `needs-decision` labels go stale after PRs
merge and decisions land. Before triage, take a quick pass: for each open issue
with a blocker, check whether all blockers are now `CLOSED`; if so it's
eligible. For `needs-decision` issues, check whether the decision was recorded
(in an issue comment or `docs/`); if so, treat it as ordinary work. This pass is
fast — always run it before triage.

## 2. Pick the next ticket

```bash
gh issue list --state open --json number,title,url,labels,body --limit 100
```

Apply this filter:

1. Issue is `OPEN`.
2. Not `needs-decision` / `question` (unless the decision has actually been made).
3. Every "Blocked by" issue is `CLOSED`.
4. No open PR already closes it: `gh pr list --state open --search "in:body Closes #<n>"`.
5. No claim comment from `implement-issue` newer than 6 hours:
   `gh issue view <n> --json comments` and look for an
   `implement-issue claiming` marker.

**Order:** `priority` label ascending, then issue number ascending. Prefer
issues that unblock the most downstream work. Pick the first match.

**Resume case:** if a prior run left a worktree under
`.claude/worktrees/issue-<n>-…` with no open PR, it's a crashed run. Pick that
issue back up and reuse the worktree (`cd` into it, `git fetch && git rebase
origin/main`).

**Nothing eligible?** Pick the highest-priority blocked / `needs-decision` item,
post a comment summarizing what's still blocking it and what would clear it, and
exit cleanly.

Once selected, comment on the issue:
`🤖 implement-issue claiming this for the next session.`

## 3. Worktree (concurrent runs can collide)

Each run uses its own git worktree so concurrent runs cannot stomp on each
other:

```bash
slug=$(echo "<issue-title>" | tr 'A-Z' 'a-z' | tr -cs 'a-z0-9' '-' | sed 's/^-//;s/-$//' | cut -c1-30)
worktree=".claude/worktrees/issue-<n>-${slug}"
branch="claude/issue-<n>-${slug}"

if [ -d "$worktree" ]; then
  cd "$worktree" && git fetch && git rebase origin/main
else
  git worktree add -b "$branch" "$worktree" origin/main
  cd "$worktree"
fi
dotnet restore clickup-todo.slnx
```

If the branch already exists from a crashed prior run, reuse it:
`git worktree add "$worktree" "$branch"` (no `-b`). `.claude/worktrees/` is
gitignored — leave it intact when exiting; pre-flight prunes stale ones.

## 4. Read the plan

Look in `.claude/plans/` for a file matching the feature/issue. If a plan
exists, read it before writing code. If none exists, enter planning mode,
produce a plan grounded in the issue's acceptance criteria, and save it to
`.claude/plans/<feature>.md` before implementing.

## 5. Phases

Break the work into 2–4 phases (e.g. spec/model → client/service → TUI → tests).
Commit and push at the end of each phase. The first push opens a draft PR;
subsequent pushes update it.

## 6. Tests

Write tests for every non-UI behavior — unit at minimum, integration
(`SkippableFact`, env-gated) where a ClickUp boundary is involved, mirroring the
patterns in `tests/ClickUpTodo.Tests/`. NEVER weaken or delete a test to make it
pass.

## 7. Implement, validate, push (per phase)

Run the full quality gate from the repo root after each phase (mirrors CI):

```bash
dotnet build clickup-todo.slnx -c Release
dotnet test  clickup-todo.slnx -c Release
dotnet format clickup-todo.slnx        # then re-build if it changed anything
```

The build must be 0 warnings / 0 errors and all tests green (integration tests
skip without `CLICKUP_TOKEN`). Then commit (HEREDOC body) and push.

Standard commit footer (per the repo's history / the maintainer's convention):

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

On the first push, open a draft PR:

```bash
gh pr create --draft \
  --title "<type>(<scope>): <summary> (#<n>)" \
  --body "$(cat <<'EOF'
## Summary

<1-3 bullets>

## Plan

Linked plan: `.claude/plans/<file>.md`

## Test plan

- [ ] ...

Closes #<n>

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

## 8. TUI changes (Terminal.Gui)

For any UI-affecting issue:

- The build must succeed; the TUI can't be exercised in CI. Describe how you
  verified it (or how the maintainer can) in the PR.
- Keep the single sectioned `ListView` model — do not add a second focusable
  pane (input-latency regression #3). New visual groupings should be header
  rows in the existing `_rows` mechanism (Tab already jumps between sections).
- Keep command shortcuts as chords / function keys; bare letters are reserved
  for the ListView type-ahead (#12).
- Include a short before/after description (or a paste of the rendered layout)
  when the change is visual.

## 9. Finalize

```bash
dotnet build clickup-todo.slnx -c Release && dotnet test clickup-todo.slnx -c Release
gh pr ready <num>
```

## 10. First-pass review via subagent

Launch a review subagent (`subagent_type=general-purpose`, a capable model).
Instruct it to:

1. Run the repo's `/code-review` skill against the diff, or do a manual deep
   review: read the full diff (`gh pr diff <num>`), check tests, and check the
   hard rules above — **especially** that `Generated/` wasn't hand-edited, the
   curated spec was used for any API changes, integration tests are skippable,
   and no second focusable pane was introduced.
2. Post comments via the GitHub MCP review tools or
   `gh api repos/rbcministries/clickup-todo-cli/pulls/<num>/comments`, or return
   them verbatim for you to post — do not paraphrase.

## 11. Address review

For each review comment: if valid, change/commit/push (the PR updates
automatically); if no change is needed, post a threaded reply explaining why.
Post a follow-up on every first-round comment so nothing is left
mid-conversation.

## 12. Cleanup & exit

- **PR is ready-for-review and pushed:** leave the worktree intact for the
  maintainer to merge / for follow-up runs.
- **Exiting early due to an unrecoverable blocker:** post a comment on the issue
  summarizing what's blocked and what would unblock it; replace your claim
  marker with a status update so the next run picks up cleanly; leave the
  worktree intact (don't delete partial work); exit cleanly.

## Scope & guardrails

- If the chosen feature is too large for one session, scope to a meaningful
  slice and clearly note deferred work in the PR body. The hour cadence is
  forgiving — better to ship a clean slice than a broken full feature.
- Keep PRs small and focused on one issue.
- Never `--no-verify`, never force-push, never amend a published commit.
- Never hand-edit generated code; never commit secrets or a real `pk_` token.

Begin.
