# Claude Code commands

Project slash commands for agentic maintenance of this repo. Run them in Claude
Code from the repo root.

| Command            | What it does |
| ------------------ | ------------ |
| `/implement-issue` | Picks one eligible open GitHub issue and delivers it end-to-end: plan → phased implementation (in an isolated git worktree) → tests → draft PR → self-review → ready-for-review. Designed to run unattended; safe to invoke manually too. |
| `/review-issues`   | Grooms the open-issue backlog: analyzes blockers/synergies, writes a dependency analysis to `docs/`, and cross-references + labels issues so `/implement-issue` sequences work sensibly. Run this first when the backlog is unstructured. |

## Hourly automation

`.github/workflows/auto-implement.yml` runs `/implement-issue` on an hourly
schedule via the Claude Code GitHub Action. It is **off by default** — see the
comments at the top of that file to opt in (add an `ANTHROPIC_API_KEY` secret and
set the `ENABLE_AUTO_IMPLEMENT` repo variable to `true`). It only ever opens
draft PRs; a human still reviews and merges.

These commands were ported/adapted from
`BenSeymourODB/next-digital-wall-calendar` and
`BenSeymourODB/linux-parental-controls-toolkit`.
