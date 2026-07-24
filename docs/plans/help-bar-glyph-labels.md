# Mouse/UX (D follow-up): tighten clickable help-bar action labels to glyphs (#343)

Follow-up to **#289** (clickable help bar, PR #342) and part of epic **#283**.
#289 shipped the clickable-footer behaviour and the movement-vs-action
classification (`HelpItem.IsAction` / `Chord` in `Tui/Screens/HelpLine.cs`);
it **deliberately deferred** the concise-label tightening so the labels could
be coordinated with the finalized keys from **E (#290)**.

**#290 has now merged** (closed 2026-07-19: Quick Updates standardized on
`Ctrl+U`, refresh on `F5 ↻`), so `HelpItemSets` already reflects the unified
keys. This slice does the remaining labels-only polish.

## Current state (verified)

`HelpItemSets.MainList` (`Tui/Screens/HelpLine.cs:157`) already uses glyph
labels for several actions — `Ctrl+B 🌐`, `Ctrl+P 📌`, `F2 ⚙`, `F5 ↻` — so a
glyph vocabulary is already established. The two remaining word-labels the
#289 acceptance criterion explicitly named for glyphing are still words:

- `F12 "completed"` — the show/hide-completed toggle (the `👁✅` example).
- (a `↑↓` "sort" affordance — but there is **no** standalone sort item on any
  footer; F3 is the combined `filter/sort/group` entry, and `↑/↓` is already
  the non-clickable *movement* glyph, so forcing `↑↓` onto F3 would mislead.)

The click machinery, hit-testing, and `Fit` truncation are unchanged by this
issue — it is purely the display `Label` strings, with every `Key` hint kept.

## Scope / decisions

Change action **labels** to concise glyphs where a widely-understood glyph
clearly reads, keeping the `Key` hint and leaving movement hints untouched:

| Set | Item | Before | After | Why |
| --- | --- | --- | --- | --- |
| `MainList` | `F12` | `completed` | `👁✅` | The issue's named example: 👁 = show/hide, ✅ = completed. Concise + discoverable. |
| `MainList` | `Ctrl+N` | `new task` | `➕` | Universal "create/new"; matches the established glyph vocabulary; frees footer columns. |
| `NotificationsFeed` | `F12` | `completed` | `👁✅` | Same toggle semantics as the list; keep the two footers consistent. |

**Intentionally left as words** (documented for the maintainer, trivially
tweakable one-liners): `Ctrl+U quick update`, `↩ detail`, `Ctrl+O open by id`,
`Ctrl+E feed`, `F3 filter/sort/group`, `F4 subtasks`, `F6 badges`,
`Ctrl+Q quit`, `F1 help`. None has an unambiguous glyph that clearly beats the
word, and `F1 help` appears in every set (a glyph there would churn ~10 sets).
Being conservative here honours the issue's own caution that glyph wording is a
subjective maintainer call.

## Invariants preserved

- **No `Generated/` hand-edit, no curated-spec change** — TUI display strings only.
- **No second focusable pane (#3)** and **no keybinding change (#12)** — labels only.
- **Movement hints unchanged** (`↑/↓`, `→|`, `→/←`, `Ctrl+→/←`, `type`).
- **Every action item still re-raises a parseable key** — `Key`/`Chord` untouched,
  so `EveryActionItem_ReRaisesAParseableKey` stays green.
- **`Fit` truncation math** — the two changed items sit past index 3, so the
  width-70 prefix test is unaffected; the other `Fit` tests assert relations
  (`wide.Count > narrow.Count`, `≤ width`) that hold as items only get shorter.

## Tests

- `HelpLineTests.Format_MainList_RendersTheFullFooter` — repin the full footer
  string with the two new glyph labels (do **not** weaken it).
- `HelpLineTests.MainList_CarriesCtrlNNewTask` — assert the New-Task action item
  with its new label.
- `HelpLineTests.Format_NotificationsFeed_RendersMoveMentionsHelpAndBack` — repin
  with `F12 👁✅`.
- `tui-validate` `help_bar_click_check.py` — retarget its `find()` anchor from
  the full `"Ctrl+N new task"` label to the stable `"Ctrl+N"` key token (the
  click column and behaviour are unchanged), confirming glyph action hints still
  fire on click and movement hints still no-op.

## Phases

1. **Labels + unit tests** — edit `HelpItemSets`, repin `HelpLineTests`, quality gate.
2. **E2E + finalize** — update `help_bar_click_check.py`, run `tui-validate`, PR.
