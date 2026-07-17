# Beta / Main Release Strategy

Status: **Draft** · Owner: maintainer · Last updated: 2026-07-17

This document defines how `clickup-todo-cli` moves from continuous PR-merging into
**named, installable releases** so it can be handed to co-workers for testing, while
staying easy for alpha-testers and contributors to clone and run.

## 1. Goals & audiences

We serve three distinct audiences with one codebase and two release channels:

| Audience | What they want | How they get it |
| --- | --- | --- |
| **Co-worker testers** (mostly non-developers) | A thing that runs, no toolchain. Paste a token, triage tasks. | A **self-contained binary** from a GitHub **Release** — no .NET install. |
| **Alpha testers / contributors** | Run bleeding-edge, file good bugs, send PRs. | `git clone` + `dotnet run` (per README). |
| **Dev-machine users** | Install once, keep it on `PATH`, update easily. | `dotnet tool install` (global tool). |

The strategy optimizes for the first group without penalizing the other two.

## 2. Release channels & versioning

We use [SemVer](https://semver.org/) with a **pre-1.0** posture (UX and config schema may
still shift between minors). Two channels, both cut from `main` as git tags — there is **no
long-lived `develop` branch** (see §3):

- **Beta channel** — prerelease tags `vX.Y.Z-beta.N` (e.g. `v0.1.0-beta.1`).
  Published as a GitHub Release marked **pre-release** and, for the tool, a NuGet
  **prerelease** package. This is the channel co-worker testers track.
- **Stable / "main" channel** — release tags `vX.Y.Z` (e.g. `v0.1.0`).
  A full GitHub Release + stable NuGet package. Promoted from a beta that has soaked
  without P1 bugs.

The package/assembly version is **driven from the tag** at release time
(`dotnet pack -p:Version=${TAG#v}`), so `<Version>` in the csproj stays the in-development
baseline and never has to be hand-bumped per beta.

**Version bumping rule (pre-1.0):**
- Bug-fix-only beta → bump `-beta.N`.
- New epic/feature completed → bump the **minor** (`0.1 → 0.2`) and restart `-beta.1`.
- We reserve `1.0.0` for the [1.0 bar](#6-roadmap-to-stable--10) below.

## 3. Branching model

Keep the current **trunk-based** flow — it already works and suits a small team:

- `main` is always releasable and always green (CI gates every PR).
- Work happens on short-lived branches (`claude/*`, `feat/*`) → PR → **squash-merge** to `main`.
- Releases are **tags on `main`**, never a separate branch.
- Hotfix for a shipped stable: branch from the stable tag, fix, tag `vX.Y.(Z+1)`,
  then cherry-pick/forward-merge to `main`. (Not expected pre-1.0; documented for completeness.)

Branch protection on `main`: require the CI check to pass and require PR review
(self-review acceptable for the solo maintainer today).

## 4. What ships in the first beta (the cut line)

The first beta ships the **finished, coherent slice** — read, triage, detail, feed, and
agent dispatch — and explicitly defers the in-progress creation/editing surfaces.

### In `v0.1.0-beta.1` (done and cohesive)

| Area | Epic / source | State |
| --- | --- | --- |
| Core triage list (assigned + Personal Tasks, status change, pin, group/sort/filter, refresh) | pre-epic core | ✅ shipped |
| Task Detail view (Description / Comments / Stream tabs, contextual help) | #102 | ✅ closed |
| Mentions & Comments feed (F-key feed, recent-activity source) | #109 | ✅ 9/9 — close on release |
| Persistent local cache & storage backend (LiteDB, staleness/TTL, closed-task prefetch) | #118 | ✅ 7/7 — close on release |
| Agent dispatch (interactive + one-off `claude` sessions, working dirs, result posting) | #23, #90 | ✅ closed |

### Land-before-cut (in-flight PRs) — ✅ merged

- **PR #300** — per-dispatch launch-location override (closes #275). ✅ merged.
- **PR #285** — `ListSelectorView` (closes #239; unblocks the New Task list selector). ✅ merged.

Both are now on `main`. Once the release workflow (§8) lands, confirm CI green and tag
`v0.1.0-beta.1`.

### Deferred to a later beta (intentionally out of the first cut)

- **Quick Updates** (#153, 90% — one child, #242, remaining). Include in `beta.2`/`0.2.0`
  once #242 lands, so status/priority/assignee editing arrives as a complete feature.
- **Writing New Content** (#208, 75% — New Task screen #240/#241/#249 remaining). Ship New
  Task once the list selector + create path is complete; partial creation UX is a bad first
  impression for testers.
- **Mouse/UX polish** (#283, 0%) and **Multi-tab / multi-instance** (#292, 0%). Large,
  fully-scoped, not started — these define the road to `0.3+` / `1.0`, not the first beta.

**Action:** close the two 100%-complete-but-open epics (#109, #118) as part of cutting
beta.1 so the milestone board reflects reality.

## 5. Beta phasing

| Tag | Theme | Gate |
| --- | --- | --- |
| `v0.1.0-beta.1` | First testable build: triage + detail + feed + dispatch | PRs #300, #285 merged; CI green; §9 checklist |
| `v0.1.0` | Promote beta.1 after soak | ≥1 week, ≥3 testers, no open P1 |
| `v0.2.0-beta.1` | Quick Updates (#153) + New Task creation (#208) complete | epics closed |
| `v0.2.0` | Promote | soak + no P1 |
| `v0.3.0-beta.x` | Mouse/UX (#283) and/or Multi-tab (#292) | per epic completion |

Cadence during active beta: aim for a **weekly** beta tag while there's merged work worth
testing; skip weeks with nothing user-facing.

## 6. Roadmap to stable & 1.0

`0.x` stable releases are "safe to use daily, schema may still move." The bar for **`1.0.0`**:

1. Creation & editing complete: Quick Updates (#153) and Writing New Content (#208) closed.
2. Mouse/UX (#283) and Multi-tab (#292) either shipped or explicitly declared post-1.0.
3. **Name/identifier decision (#39) resolved** — see §10; the public NuGet package id is
   effectively permanent, so this must be settled before the first *public* NuGet publish.
4. Config schema considered stable (documented migration path already exists via LiteDB).
5. Two consecutive betas with no P1 bugs.

## 7. Distribution & install paths

### 7a. Co-worker testers — GitHub Release binaries (primary)

Publish **self-contained, single-file** builds so testers need **no .NET install**:

```bash
dotnet publish src/ClickUpTodo/ClickUpTodo.csproj -c Release \
  -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:Version=${TAG#v}
```

**Beta.1 ships `win-x64` only.** It's the primary (Windows) audience, it's the best-tested
target, and it's the only OS where the ClickUp token is **encrypted at rest** (Windows DPAPI,
current-user scope). On non-Windows the token store falls back to an **unencrypted file** and
macOS is untested end-to-end, so `linux-x64` and `osx-arm64` are held out of the release matrix
(commented in `release.yml`, one line to re-enable). **Re-enabling them is gated on the
cross-platform readiness epic (#312)** — see §11. Contributors on any OS run from source via
`dotnet run` regardless. A one-page
[beta tester guide](#10-feedback--contribution-loop) tells testers: download, run, paste token,
known limitations (including the SmartScreen prompt on the unsigned exe).

### 7b. Dev-machine users — .NET global tool

The csproj already packs as a tool (`PackAsTool`, `ToolCommandName=clickup-todo`). Once we
publish to NuGet:

```bash
dotnet tool install --global ClickUpTodo.Cli --version 0.1.0-beta.1
```

Until a public NuGet id is decided (#39), install from the Release-attached `.nupkg` with
`--add-source`, exactly as the README documents today.

### 7c. Contributors — clone & run

Unchanged from the README: `dotnet run --project src/ClickUpTodo`, `dotnet test`, and the
`tui-validate` skill for rendering changes.

## 8. Release automation

A tag-triggered [`.github/workflows/release.yml`](../.github/workflows/release.yml) implements
this, complementing the existing `ci.yml` (build + test on `main`/PRs):

- **Trigger:** push of a tag matching `v*`.
- **`test` job (gate):** restore → build → `dotnet test`; the rest of the pipeline never runs
  on a red build.
- **`build` job (matrix):** one self-contained, single-file executable per RID
  (`win-x64`, `linux-x64`, `osx-arm64`), each on its **native OS** runner so ReadyToRun is
  valid; version injected via `-p:Version=${TAG#v}`; staged as
  `clickup-todo-<version>-<rid>[.exe]`.
- **`release` job:** `dotnet pack` the global tool → gather all binaries → `gh release create`
  with `--generate-notes`, marking it **pre-release** when the tag version contains a `-`
  (e.g. `-beta.1`). `dotnet nuget push` is intentionally left out until the package id is
  settled (#39).
- **Release notes:** auto-generated from PR titles since the previous tag; hand-edit a short
  "highlights + known issues" header before or just after publishing.

**Cutting the first beta** (once this PR is on `main`):

```bash
git checkout main && git pull
git tag v0.1.0-beta.1
git push origin v0.1.0-beta.1   # -> workflow builds binaries + tool, publishes a pre-release
```

## 9. Release checklist (per tag)

1. All intended PRs merged; `main` CI green.
2. `dotnet test clickup-todo.slnx` green locally; `tui-validate` run for any rendering-touching change.
3. Close any epics that hit 100% (e.g. #109, #118 for beta.1).
4. Draft release notes: highlights, known limitations, the ClickUp-token setup reminder,
   and the mentions-automation caveat (`docs/mention-assignee-automation.md`).
5. Tag `vX.Y.Z[-beta.N]` on `main` and push → release workflow produces artifacts.
6. Verify the `win-x64` binary launches and completes first-run setup on a clean machine.
7. Announce to testers with the download link + tester guide.

## 10. Feedback & contribution loop

- **Beta tester guide** — add `docs/beta-testing.md` (or `TESTING.md`): download link,
  first-run setup, what works / what's deferred, and how to report a bug.
- **Contributor guide** — add `CONTRIBUTING.md`: build/test, the `tui-validate` gate,
  the plan-then-issue workflow (`.claude/plans/`, `implement-issue`), and PR conventions
  (Conventional-Commit titles, "Closes #N", squash merge).
- **Issue templates** — `.github/ISSUE_TEMPLATE/` for `bug` and `enhancement`; a
  `beta-feedback` label to triage tester reports quickly.
- **Feedback channel** — a pinned "Beta feedback" issue (or GitHub Discussion) as the
  low-friction catch-all for non-developer testers.

## 11. Open decisions / blockers

- **#312 — cross-platform (macOS/Linux) release readiness (gate for non-Windows binaries).**
  The `linux-x64` / `osx-arm64` release artifacts stay disabled until this epic's
  investigations are resolved or explicitly accepted as non-blocking. Children:
  - **#306** — secure token storage at rest (today the token is stored **unencrypted** on
    non-Windows; DPAPI is Windows-only).
  - **#307** — agent-dispatch terminal launch (Windows-centric today).
  - **#308** — open-in-browser (`open` / `xdg-open`).
  - **#309** — TUI rendering & keybindings (F-keys, Option/Alt, glyph width, color).
  - **#310** — macOS Gatekeeper/quarantine & Linux exec bit for distributed binaries.
  - **#311** — clean-machine first-run smoke test on each OS.

  This gate does **not** affect the Windows beta or contributors running from source.
- **#39 — name/identifier rename.** Decide the public command name, package id, and
  namespace *before* the first public NuGet publish; the id is effectively permanent.
  Not a blocker for GitHub-Release-binary betas.
- **Code signing.** Windows SmartScreen will warn on an unsigned single-file exe. For beta,
  document "click More info → Run anyway"; consider a signing cert before wide/stable rollout.
  (macOS notarization is tracked under #310.)
- **#2 — ClickUp API v3.** A vendor-dependent watch item, **not** a release blocker.
- **License/attribution.** MIT is set; the OpenAPI-spec provenance note in the README covers
  the generated client. No action needed for release.
