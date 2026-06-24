# Review issues

Do a review of all open issues on this repository (`rbcministries/clickup-todo-cli`).
Identify blockers, co-dependencies, and synergies between issues: which must be
addressed first to let others proceed, and which aren't strictly blocked but
become easier once others land. For each connection you identify, update the
description of both issues tagging the connected issue. This grooming makes
`/implement-issue` sequence work sensibly.

> Where this shows `gh ...`, use the GitHub MCP tools (`mcp__github__*`) instead
> when your environment provides them; fall back to the `gh` CLI locally.

## Instructions

1. **Fetch all open issues:**
   `gh issue list --state open --json number,title,labels,body --limit 100`

2. **Analyze dependencies** across all issues. For each, identify:
   - **Blockers**: issues that must be completed before this one can start
     (e.g. a curated-spec / `TaskItem` field must exist before a UI that uses it).
   - **Enables**: issues this one unblocks once completed.
   - **Synergies**: issues that share infrastructure/patterns (e.g. the settings
     dialog, the status cache, the sectioned-list rendering) and benefit from
     coordinated design.
   - **Benefits from**: issues that aren't strict blockers but would make this
     one easier if done first.

3. **Group into tiers** by dependency depth:
   - **Tier 0 (Foundation)**: no blockers, enables many others.
   - **Tier 1**: depends only on Tier 0 or external factors.
   - **Tier 2 / 3+**: deeper dependency chains.

4. **Identify synergy clusters**: groups that share enough infrastructure that
   they should be designed together even if developed separately.

5. **Write two documentation files:**
   - `docs/issue-dependency-analysis.md` — full dependency graph with tiers, a
     per-issue connection map, recommended implementation order, and synergy
     clusters.
   - `docs/issue-cross-reference-updates.md` — the exact markdown to append to
     each affected issue's description.

6. **Update each affected issue** on GitHub:
   - Append a "Cross-References" section to its description.
   - Preserve existing body: `gh issue view <n> --json body -q .body`, then
     `gh issue edit <n> --body "<existing + new>"`.
   - The section should tag connected issues (`#<n>`) and explain each
     connection's nature (blocks / enables / synergy / benefits-from).
   - Apply a `priority` label (`p0`–`p2`) and/or a `blocked` label where the
     analysis warrants it, so `/implement-issue` can order work.

7. **Report a summary** of all updates made.
