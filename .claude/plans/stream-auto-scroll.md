# Plan — S2: Auto-scroll the Stream to newest or oldest on load (#107)

Part of epic #102 (task detail view improvements). Builds on #106 (Stream tab, merged
#150). Lets the detail view **open scrolled to the newest — or oldest — entry** in the
Stream timeline, so the user lands on the most recent activity without paging. The
persisted setting/toggle that chooses the preference lands in **#108 (S3)**; this issue
ships the mechanism and a sensible default.

## Acceptance criteria (from the issue)

- After the Stream body is populated, position the viewport at the top or bottom per the
  configured preference. Applies when the view opens (`OnShown`) and when the Stream body
  is re-rendered after a sort-direction toggle (`SetStreamSort`, #106) — re-assert the
  intended edge.
- Preference is expressed relative to **content meaning** ("newest" / "oldest"), not raw
  top/bottom, and stays correct across both sort directions:
  - `Ascending` (oldest-first: Description/oldest at top → newest at bottom): Newest =
    bottom, Oldest = top.
  - `Descending` (newest-first: newest at top → Description/oldest at bottom): Newest =
    top, Oldest = bottom.
- Programmatic scroll uses the framework's scroll API (verified against TG 2.4.10, not
  guessed): `TextView.MoveEnd()` scrolls to the last line, `TextView.MoveHome()` to the
  first. (The issue floated `TopRow` — **it does not exist** on TG 2.4.10's `TextView`;
  `MoveEnd`/`MoveHome`/`ScrollTo(Point)` do.) Wrapped in a small helper so it's centralized;
  no custom scroller (#63 caution).
- Cheap and flicker-free (set in `OnShown`, before the user interacts).

## Design decisions

- **`StreamAutoScroll` enum** (`Newest` / `Oldest`) in `Configuration/` (one-enum-per-file,
  like `BadgeDisplay`). It is a user preference that #108 will persist in `ViewSettings`;
  putting it in `Configuration` keeps the dependency direction correct (Configuration must
  not reference Tui). This issue does **not** add the `ViewSettings` property or F2 UI —
  that's #108's scope; the seam below lets #108 plug in with no rework.
- **Pure edge-resolution** in a new `Tui/Screens/DetailScrollModel.cs` (same pure-glue split
  as `DispatchPaneModel`/`StatusPickerModel`): `Edge ResolveEdge(StreamAutoScroll, StreamSort)`
  returning `Edge.Top` / `Edge.Bottom`. `ResolveEdge` lives in Tui because it combines the
  config enum with `StreamSort` (which lives in Tui); Tui already depends on Configuration.
  Fully unit-tested (the issue's required "sort + preference → top/bottom" test).
- **Seam for #108.** `TaskDetailScreen` gains a **defaulted** constructor parameter
  `StreamAutoScroll autoScroll = StreamAutoScroll.Newest`, stored in a field. `TodoApp`
  (the only constructor caller) needs no change today; #108 passes the persisted value.
- **Default = `Newest`.** The feature's purpose is to land on the latest activity; shipping
  it as a no-op (Oldest = current top-of-list behaviour) would be an odd thing to ship and
  impossible to demonstrate. With the Stream's default `Ascending` sort this means the view
  opens scrolled to the bottom (newest comment). The Description stays one keystroke away
  (Home / ↑ / the Description tab). **#108 owns making this user-configurable and may revisit
  the default** — noted in the PR.
- **Scope: the Stream tab only.** Auto-scroll is defined relative to the Stream's sort
  direction; the Comments tab renders in ClickUp's native returned order (no `StreamSort`),
  so "newest/oldest" has no defined mapping there. Deferred/out of scope; noted in the PR.
- **No new keybinding**, so `HelpItemSets.Detail` is unchanged (the toggle is #108's F2 UI).

## Phases

1. **Enum + pure model + tests.** Add `StreamAutoScroll`, `DetailScrollModel.ResolveEdge`,
   and `DetailScrollModelTests` (all four preference×sort combinations, both `Edge` values).
   Commit + push (opens draft PR).
2. **Screen wiring.** Thread the defaulted ctor param, add `ApplyStreamAutoScroll()` calling
   `_streamPane.MoveEnd()`/`MoveHome()`, invoke it from `OnShown` and `SetStreamSort`. Build
   0/0; commit + push.
3. **Validate + finalize.** Full quality gate; `tui-validate` to confirm the Stream opens at
   the newest entry (last line visible) and re-asserts on a sort toggle; `gh pr ready`;
   subagent review.

## Non-goals (deferred, tracked by sibling issues)

- Persisted preference + F2 toggle for the auto-scroll position → **#108 (S3)**.
- Auto-scroll for the Comments tab (needs a defined order there first).
