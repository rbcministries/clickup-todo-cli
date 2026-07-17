# Contributing

Thanks for helping build **clickup-todo-cli** — a keyboard-driven Terminal.Gui (v2) TUI for
ClickUp tasks. This guide is for **alpha testers and contributors** who want to run the app
from source and send changes. If you just want to *use* a beta build, see the
[beta tester guide](docs/beta-testing.md) instead.

## Prerequisites

- **[.NET 10 SDK](https://dotnet.microsoft.com/download)** (the app targets `net10.0`).
- **PowerShell** (`pwsh`) — only needed if you regenerate the ClickUp API client.
- A ClickUp **personal API token** (Settings → Apps → API Token, starts with `pk_`) if you
  want to run against your own workspace or run the integration tests.

## Clone & run

```bash
git clone https://github.com/rbcministries/clickup-todo-cli.git
cd clickup-todo-cli
dotnet run --project src/ClickUpTodo
```

On first launch the app walks you through setup (token → workspace → Personal Tasks list →
refresh interval). `clickup-todo --reset` starts over. See the README for keyboard shortcuts,
driver options (`--driver`), and OAuth.

## Build & test

```bash
dotnet build clickup-todo.slnx
dotnet test  clickup-todo.slnx
```

- Unit tests (config, token storage, pure models) always run.
- **Integration tests hit the real ClickUp API and must be `SkippableFact`** — they skip
  automatically when `CLICKUP_TOKEN` is absent, so CI stays green without credentials. Provide
  `CLICKUP_TOKEN` (and optionally `CLICKUP_WORKSPACE_ID`, `CLICKUP_LIST_ID`) to exercise them.

CI (`.github/workflows/ci.yml`) runs `restore → build → test` on every PR to `main`. **A PR
must be green before review.**

## Validating TUI changes

For changes that touch **rendering, keypress handling, the list source, or driver/output
code**, validate end-to-end with the **`tui-validate`** skill
(`.claude/skills/tui-validate/SKILL.md`): it runs the real app under a PTY against a fake
ClickUp backend and asserts on a pyte-emulated screen.

**Run it only after `dotnet test` is fully green** — the PTY harness is slower and noisier, and
a logic bug shows up there as a confusing rendering symptom. Chase unit-level failures first.

## Architecture & hard rules

Source lives in `src/ClickUpTodo/`: TUI in `Tui/`, domain logic in `Services/`, the ClickUp API
facade in `ClickUp/` over a Kiota-generated client in `ClickUp/Generated/`. A few
non-negotiables (see `CLAUDE.md` and `.claude/commands/implement-issue.md` for the full set):

- **Never hand-edit the generated client** (`ClickUp/Generated/`). To add ClickUp fields or
  endpoints, edit the curated spec `ClickUp/clickup-openapi.json`, then regenerate:
  ```bash
  dotnet tool restore
  pwsh scripts/regen-client.ps1
  ```
  Map new fields into the stable domain records in `ClickUp/Models.cs` via the `ClickUpClient`
  facade — the rest of the app must not see generated types.
- **ClickUp auth quirk:** personal tokens go in a **raw `Authorization` header (no `Bearer`)**,
  handled by `ClickUpTokenAuthProvider`. Don't "fix" this to `Bearer`.
- **Keep input responsive:** the main view is intentionally a single sectioned `ListView`. Do
  **not** reintroduce a second focusable pane — it caused the input-latency regression in #3.
- **Tests land with the code.** Put logic in testable services and unit-test it; never weaken
  or delete a test just to make it pass.

## How work is organized

- Work is tracked as **GitHub issues**, often grouped under an **`Epic: …`** issue with native
  sub-issues. Larger features carry a short design note in **`.claude/plans/`**.
- Before starting something non-trivial, comment on the issue (or open one) so effort isn't
  duplicated.

## Branching, commits & PRs

- **Branch** off `main` with a short-lived branch (`feat/…`, `fix/…`, or `claude/…`).
- **Commit titles follow [Conventional Commits](https://www.conventionalcommits.org/) with a
  scope**, matching the existing history:
  `feat(feed): …`, `fix(tui): …`, `refactor(selector): …`, `test(config): …`, `docs: …`,
  `ci(release): …`.
- **Open a PR to `main`** and reference the issue it resolves with `Closes #NNN`. Fill in a
  short summary, a test plan, and — for TUI changes — how you verified them.
- PRs are **squash-merged**, so the PR title becomes the commit; keep it Conventional-Commit
  shaped.
- Keep PRs focused; match the surrounding code's style, naming, and comment density.

## Reporting bugs & proposing features

Open an issue: for bugs, include your OS, terminal, the active `--driver` (shown in the status
line), and steps to reproduce. Beta testers can also use the
[beta tester guide](docs/beta-testing.md#found-a-bug)'s reporting steps.

## License

By contributing you agree that your contributions are licensed under the
[MIT License](LICENSE), the same license as the project.
