# Plan — OSC-8 hyperlinks for markdown `[text](url)` links (#430)

Follow-up to **#380** (PR #429, merged), part of epic #313. #380 emits OSC-8 terminal
hyperlinks for **bare** links in the Description / Comments / Stream panes — the case where a
link's on-screen text *is* its URL. It deliberately skipped **markdown `[text](url)`** links,
whose true target (`LinkSpan.Url`) differs from the visible text, because #380's draw-time URL
derivation (`DetailPaneView.LinkUrlForCell`) reconstructs the target from the *drawn cells* and
so can only ever recover the visible text (prose) — its URL-revalidation guard makes markdown
links **safely skip** (no OSC-8 rather than a wrong target). This issue closes that gap.

## Key realisation — no `WordWrapManager` needed for the common case

The issue text frames the target as unrecoverable without the display→source-line mapping owned
by Terminal.Gui's `internal WordWrapManager`. That is only true when the `[text](url)` markup is
**split across two rendered rows**. In the panes, the body TextView renders the **raw** markup
(`TaskLinkExtractor` tags only the *visible-text* cells between `[` and `]` as a link; #317), so
when the whole `[text](url)` sits on **one** rendered row — the unwrapped common case, and the
case the acceptance criteria + a wide-`COLS` `tui-validate` seed exercise — the resolved target
is recoverable by **re-extracting links from that row's own text**, exactly the offset-free
technique #413 (PR #440) already uses for the wrapped-line **underline**
(`DetailPaneView.ClassifyRowLinkCells`).

So this rides #413's per-row re-extraction instead of a caret-sweep or an internal-API mapping:
purely additive, fully unit-testable, and with the same low blast radius as #413.

## Design

`DetailPaneView` today has two draw-time link derivations that disagree on wrapped rows:

- **styling** — `ClassifyRowLinkCells` re-extracts links from the rendered row's graphemes
  (correct on every wrapped row; #413), and
- **OSC-8 URL** — `LinkUrlForCell` reconstructs the target from the per-cell tags
  (`ClassifyCell`), which word wrap misaligns and which never carries a markdown target (#380).

Unify them onto the per-row re-extraction, extended to also carry each cell's **resolved
`LinkSpan.Url`**:

1. **Private core `ClassifyRow(row) → (DetailCellStyle[] Styles, string?[] Urls)`** — one per-row
   re-extraction (`TaskLinkExtractor.Extract` over the row's reconstructed graphemes, the same
   grapheme-length offset accounting as `ClassifyRowLinkCells`). For each cell inside a link
   span it records both the kind style **and** that span's `LinkSpan.Url` (the *resolved* target:
   for a bare link that is the URL itself; for a markdown link its true destination). The cheap
   `IndexOf("http")` bail-out is preserved.
2. **`ClassifyRowLinkCells(row)`** keeps its public signature, now `=> ClassifyRow(row).Styles`
   (behaviour byte-identical — same extraction, same style assignment).
3. **New public pure `RowLinkUrls(row) → string?[]`** `=> ClassifyRow(row).Urls` — the per-cell
   resolved OSC-8 target, `null` for non-link cells. Unit-tested.
4. **`LinkUrlForCell(row, idxCol)`** reimplemented on `RowLinkUrls` (re-extraction based). A
   markdown link's visible-text cells now return the **resolved target**; a bare link is
   unchanged. Its doc comment is rewritten (the #380 "returns only the on-screen text / markdown
   skips" contract is replaced by "returns the resolved `LinkSpan.Url`").
5. **Draw path** caches both arrays per rendered row from a single `ClassifyRow` call (parallel
   to today's `_linkRowStyles` cache): `OnDrawReadOnlyColor` sets the OSC-8 URL from the cached
   URL array and the style from the cached style array. `OnDrawComplete` still clears the URL.

Because a keyboard-focused link (#319) only changes a cell's *attribute*, not its grapheme, the
re-extraction is focus-agnostic — a focused bare link still emits its OSC-8 hyperlink.

## Scope boundary (deferred, already tracked)

- **A markdown link whose `[text]` and `(url)` wrap onto different rendered rows.** Per-row
  re-extraction can't see across the wrap boundary: the visible-text fragment (`… [text`) holds no
  complete markdown link and gets **no** OSC-8, while the trailing `(url)` fragment re-extracts as
  an ordinary **bare** link to that URL itself (the `)` trimmed). So a split markdown link degrades
  to *less-rich* linking (the visible text is left unlinked; the raw URL becomes self-linked) —
  **never a wrong target**. Correctly hyperlinking the visible text across the wrap needs the
  display→source-line mapping this issue deliberately doesn't build; it is exactly the
  `[text]`/`(url)`-split case already tracked by **#443**. Noted in the PR as the shared-machinery
  follow-up.
- **A bare `http(s)` URL sitting inside a markdown link's visible `[text]`, when the link wraps.**
  Pre-existing extraction behaviour (shared with #413 styling): the split visible-text fragment's
  own bare URL is extracted and self-linked, rather than the markdown target. Still never a hidden
  target (the emitted URL is the one the reader sees); folded into the #443 follow-up.
- **A bare URL longer than the pane width, hard-wrapped mid-URL** — unchanged from #380/#413;
  also #443.

## Tests

- **Unit** (`DetailPaneViewTests`):
  - New `RowLinkUrls`: the resolved target on every visible-text cell of a markdown link (first /
    middle / last), the URL on every cell of a bare web and task link, the correct per-link URL
    when a bare and a markdown link share a row, `null` for surrounding prose / a link-free row /
    a separator line, and the #443 split-case safe-degradation pinned on **both** sides — `null` on
    the `[text` visible-text fragment, and a plain bare-link target on the `(url)` fragment (a
    correct URL, never a wrong target).
  - The two `LinkUrlForCell` markdown tests are **updated** to the #430 behaviour: the
    visible-prose case now returns the resolved target (renamed
    `…ReturnsTheResolvedTarget_ForAMarkdownLink`), and the visible-text-is-a-URL case now returns
    the markdown **target**, not the displayed URL (renamed accordingly). All other
    `LinkUrlForCell` tests (bare web/task, non-link, two-links, out-of-range, focused) are
    unchanged and still pass. `ClassifyRowLinkCells` tests are unchanged.
- **E2E** (`tui-validate`, new `markdown_osc8_check.py`): with an env-gated markdown link seeded
  into the Description (`E2E_MD_LINK=1`, so every existing check stays byte-identical), boot the
  app, open Task Detail, and assert on the **raw byte stream** (the harness's documented
  raw-bytes-for-escape exception) that the seeded markdown link's *visible text* is wrapped in a
  bounded OSC-8 escape targeting its **resolved** URL (not the visible text). Fixed wide `COLS`
  so the markup doesn't wrap (the wrapped case is #443). Regressions: `osc8_link_check.py` (#380
  bare-link OSC-8), `link_check.py` / `link_wrap_check.py` (#317/#413 styling), and
  `detail_check.py` A/B all stay green.

## Hard rules honoured

- No `Generated/` hand edits; no spec change / no regen (pure UI helper + a data-only draw-path
  rewire).
- Single sectioned `ListView` input model untouched — no new focusable pane / keybinding / driver
  change (the OSC-8 escape is emitted by the stock ANSI output from `IDriver.CurrentUrl`, exactly
  as #380 established).

## Phases

1. **Core + unit tests** — `ClassifyRow` split, `RowLinkUrls`, `LinkUrlForCell` reimpl, draw-path
   rewire; unit tests. Build/test/format green. Commit/push → open draft PR.
2. **E2E** — env-gated markdown seed + `markdown_osc8_check.py` + `SKILL.md` registration. `gh pr
   ready`.
