# Plan — Name the tui-validate checks instead of numbering them (#485)

Sub-issue **A** of the E2E harness epic (#484). Documentation-only, independent, safe to
land with PRs open — it removes an entire merge-conflict class from
`.claude/skills/tui-validate/SKILL.md` without touching any production or test code.

## Problem

`SKILL.md` numbers its checks sequentially (`**1. …**` … `**16. …**`, plus the `6b`/`10b`
positional special-cases) and cross-references them positionally (`check 6`, `check 7`,
`check 8`, `check 10`, `check 13`). Two costs:

1. **Every new check appends a number at the same `**N. …**` anchor**, so two PRs adding a
   check conflict on `SKILL.md` (exactly what happened between #479 and #472).
2. **Cross-references are positional**, so inserting a check mid-list forces a renumbering
   cascade — which is why `6b`/`10b` exist rather than a clean renumber. Those suffixes are
   the smell.

## Change

Identify each check by its **script filename** — already unique and stable — as the heading,
keeping the human description after it:

- `**1. Keypress latency**` → `` **`drive.py`** — keypress latency ``
- `**6b. …WRAPPED lines**` → `` **`link_wrap_check.py`** — in-text link styling on wrapped lines ``
- Cross-references: `check 8` → `` `link_click_check.py` ``, `check 7` → `` `tab_boundary_check.py` ``, etc.

The filename comes from each check's own command block (verified all 19 exist under
`tests/ClickUpTodo.Tui.E2E/`), not guessed — so e.g. check 1 is `drive.py` (not the
`latency_check.py` the issue sketched illustratively) and check 2 is `screen_check.py`.

### Heading → filename map (order preserved)

| Old | Filename |
| --- | --- |
| 1 | `drive.py` |
| 2 | `screen_check.py` |
| 3 | `color_check.py` |
| 4 | `detail_check.py` |
| 5 | `closed_bridge_check.py` |
| 6 | `link_check.py` |
| 6b | `link_wrap_check.py` |
| 7 | `tab_boundary_check.py` |
| 8 | `link_click_check.py` |
| 9 | `link_tab_check.py` |
| 10 | `osc8_link_check.py` |
| 10b | `markdown_osc8_check.py` |
| 11 | `thread_check.py` |
| 12 | `single_task_tree_check.py` |
| 13 | `detail_arrow_check.py` |
| 14 | `checklist_check.py` |
| 15 | `mention_check.py` |
| 16 | `single_task_title_check.py` + `single_task_title_refresh_check.py` |

Check 16 runs two scripts, so its heading names both — retaining the `6b`/`10b` retirement
goal (no positional suffixes anywhere).

## Scope / non-scope

- **In:** `.claude/skills/tui-validate/SKILL.md` headings + cross-references only.
- **Out:** any production code, test code, `Program.cs`, or the `*_check.py` scripts. The
  `E2E_*` invocations and commands are unchanged verbatim — only headings and the prose
  `check N` references change.
- Ordering (roughly execution order) is preserved — this is a labelling change, not a
  reorganisation.

## Verification

- No numeric `check N` reference remains in `SKILL.md` (grep for `check [0-9]`).
- Every heading is a real filename present under `tests/ClickUpTodo.Tui.E2E/` (grep each).
- Every documented command still runs verbatim (no command-line bytes changed).
- Since no script or code changes, all `*_check.py` checks are unaffected and still pass.
