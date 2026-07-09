# Plan — S3: Detail-view settings in F2 (default tab, stream sort, auto-scroll) (#108)

Part of epic #102. Blockers #106 (Stream tab) and #107 (auto-scroll) are both merged. Exposes
and persists the detail-view preferences those consume: **default tab**, **default stream sort
order**, and **auto-scroll position**.

## Acceptance criteria (from the issue)

- Three settings, persisted in `config.json`:
  - **Default tab** — Stream / Description / Comments / Other. **Default = Stream.**
  - **Stream sort order** — Ascending (oldest/Description first) / Descending (newest first).
    **Default = Ascending** (matches #106).
  - **Auto-scroll position** — Newest / Oldest. **Default = Newest** (matches #107).
- Only **sort order** is also toggleable on the detail screen itself (Ctrl+PgUp/PgDn, #106);
  default tab + auto-scroll are F2-only. The on-screen sort toggle is a per-view override and
  does **not** persist back as the new default (issue's recommended reading).
- F2 UI uses the existing cycling-`Button` pattern (like PreferredTerminal / AgentWorkingDirectory).
- Backward-compatible: an old `config.json` with no detail-view block loads to the defaults.
- `TaskDetailScreen` consumes all three on open.

## Design decisions

- **Home: a dedicated `DetailViewSettings` group on `AppConfig`**, not `ViewSettings`. This mirrors
  the `BadgeDisplay` precedent (a cosmetic detail/display pref kept off `ViewSettings` so it's
  independent of the F3 filter/sort/group view and its `IsDefault`). Avoids perturbing
  `ViewSettings.IsDefault` entirely. Backward-compat is free: an absent `detailView` key leaves the
  property at its `new DetailViewSettings()` initializer (all defaults) — no migration needed.
- **One source of truth for the enums (the issue's coordination ask):**
  - **Move `StreamSort`** from `Tui` (`TaskDetailFormatter.cs`) to `Configuration/StreamSort.cs` so
    both the formatter/screen (Tui) and `DetailViewSettings` (Configuration) reference the same enum.
    (Configuration must not depend on Tui, so the shared enum must live in Configuration.)
  - **`StreamAutoScroll`** already lives in Configuration (from #107).
  - New **`DetailTab { Stream, Description, Comments, Other }`** in Configuration — declared in the
    same order as `TaskDetailScreen`'s tab array; a pure `ToTabIndex()` helper (unit-tested) maps it
    to the index rather than relying on the ordinal implicitly.
- **Pure, testable cycling:** `Next()` extension methods on each enum (like `BadgeDisplayExtensions`),
  used by the F2 cycle buttons and unit-tested for wraparound.
- **`SettingsResult`** gains a `DetailViewSettings DetailView`; `TodoApp` saves it and passes
  `_config.DetailView` when constructing `TaskDetailScreen` (superseding #107's `StreamAutoScroll`
  ctor param, folded into the settings object).
- Single sectioned `ListView` model (#3) untouched; no new focusable pane. F2 gains three cycle
  buttons in the existing layout — no new screen.

## Phases

1. **Model + enums + tests.** Move `StreamSort`; add `DetailTab`, `DetailViewSettings`,
   `AppConfig.DetailView`, `Next()`/`ToTabIndex()` helpers. Tests: ConfigStore round-trip +
   persisted-as-string + backward-compat (old config → defaults), enum `Next()` cycles, `ToTabIndex`
   mapping. Commit + push (opens draft PR).
2. **F2 UI + wiring.** Add the three cycle buttons + `SettingsResult.DetailView` in `SettingsScreen`;
   `TodoApp` saves `_config.DetailView` and passes it to `TaskDetailScreen`; `TaskDetailScreen`
   consumes default tab (initial `_tabs.Value` + `OnShown` focus), default sort (initial render), and
   auto-scroll. Build 0/0; commit + push.
3. **Validate + finalize.** Full gate; `tui-validate` (F2 renders the new controls; detail opens on
   the configured tab/sort/scroll; A/B no regression); `gh pr ready`; subagent review.

## Non-goals

- No new on-screen toggles beyond the existing Ctrl+PgUp/PgDn sort chord (#106).
- Per-view sort override persistence is intentionally excluded (F2 is the default source of truth).
