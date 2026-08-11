# Plan: focusable-form native-modal A/B — Filter·Sort·Group (#554)

The gate between the #404 spike's "the nested-`Application.Run` architecture is
sound" and #402 actually committing the transient-modal migration. #404 proved
viability on a **non-focusable** surface (F1 Help — a static `Label`). This slice
repeats the A/B on a **focusable form** modal so the #3/#38 latency invariants —
which are fundamentally about *focusable-input* responsiveness (typing in a
`TextField`, `Tab`-cycling `ListView`s/`Button`s) — are actually measured, and so
the extra thing Help never did, **marshalling a `Result` back to the host**, is
exercised.

Filter·Sort·Group (F3) is the ideal candidate (from #554): it already has an E2E
check (`fsg_check.py`), and it returns a `ViewSettings?` — so a native variant
measures both intra-modal latency *and* result-marshalling.

## Constraints (inherited from #404 / CLAUDE.md)

- **No `Generated/` hand edits, no `clickup-openapi.json` change, no Kiota
  regen.** Pure TUI/host code + a harness check.
- **Flag-gated, off by default.** Reuse the existing `CLICKUP_TODO_NATIVE_MODAL`
  env gate (`NativeModalSpike.Enabled`); production stays byte-identical — F3 still
  mounts the `_screens` `FilterSortGroupScreen`.
- **Single sectioned `ListView` model untouched** (#3). The native modal is a
  transient sub-surface with its own focusable controls, exactly like the
  `_screens` `FilterSortGroupScreen`/`SettingsScreen` already are — it is **not** a
  second focusable pane on the main list.
- **Nested-run dispose routed through `TuiTeardown.DisposeSwallowingTeardownBug`**
  (the #346 guard), as #404's help dialog does.
- Command shortcuts stay chords / function keys; no new bare-letter binding
  (F1/F3 are function keys; bare letters stay reserved for type-ahead #12).

## Design

### 1. Extract the shared form (`FilterSortGroupFormBuilder`)

The A/B must differ **only in the hosting mechanism**, not the form — mirroring how
#404 extracted `HelpScreen.ShortcutsText` so both help hosts render the same body.
So factor the control-building + behaviour currently inline in
`FilterSortGroupScreen`'s constructor into a static
`FilterSortGroupFormBuilder.Build(ViewSettings current, Action<string> flash,
Action close)` returning a handle:

- `IReadOnlyList<View> Controls` — the field/operator/value pickers, add/remove,
  active-filter list, sort/direction/group pickers, Save/Cancel/Reset buttons.
- `View PrimaryFocus` — the field `ListView` (initial focus).
- `ViewSettings? Result` — set by the Save handler (`FilterSortGroupForm`-computed,
  preserving F4 Subtasks / F12 Completed), left null on Cancel/Reset-then-close.

Per-form keys (value-field Enter = add, filters-list Delete/Backspace = remove)
stay in the builder; `flash` surfaces invalid-rule errors; `close` is the host's
close (Screen `Close` in A, `Application.RequestStop(dialog)` in B).

`FilterSortGroupScreen` becomes a thin `Screen` that mounts the handle's controls,
forwards its `Result`, and keeps its `KeybindingDispatcher` (Esc = Back, F1 = Help)
and `HelpItems` footer. **Leg A behaviour is unchanged** — same controls, same
order, same handlers — so `fsg_check.py` stays green.

### 2. Native variant (`NativeModalSpike.RunFilterSortGroupDialog`)

A sibling of `RunHelpDialog`: build a `Dialog` (title carries `TitleMarker` so the
harness can prove leg B took the native path), mount the **same builder** controls,
run a nested `Application.Run(dialog)`, then marshal `handle.Result` to the host via
an `apply` callback and dispose through the teardown guard.

- Its own open slot (`_fsgOpen` + `TryBeginOpenFilterSortGroup`) — the native path
  pushes nothing to `_screens`, so `ActiveScreen` can't serialise it (same reason
  #404 added `_open`/`TryBeginOpen` for Help). Distinct from the help slot so Help
  can **stack over** F3.
- **Modal stacking:** the dialog's F1 defers `RunHelpDialog` via `Application.Invoke`
  (never re-entrant from inside `KeyDown`), so F1-over-F3 opens native Help on top —
  the native analogue of the `_screens` LIFO stack `fsg_check.py` exercises today.
- Esc / Cancel → `RequestStop` with a null result (no change); Save → result set,
  then `RequestStop`; on return, `apply(result)` runs on the UI thread (the nested
  loop returns to the outer loop's invoke) → `ApplyViewSettings`.

### 3. Host wiring (`TodoApp.OpenViewSettings`)

Branch on `NativeModalSpike.Enabled` exactly like `ShowHelp`: when set, claim the
FSG slot and `Application.Invoke(() => RunFilterSortGroupDialog(_config.View,
ApplyViewSettings, Flash))`; else the existing `_screens` path. `SingleTaskApp` has
no F3, so only `TodoApp` is wired.

## Measurement (`tests/ClickUpTodo.Tui.E2E/fsg_modal_check.py`)

Modelled on `native_modal_spike_check.py` (ANSI driver, diff-flush on), A =
`_screens` form, B = native `Dialog`:

1. **F3 open→paint latency** (marker: "Add a filter"). A vs B.
2. **Intra-modal key→paint latency** — the measurement #404 could not make:
   focus the value `TextField` and type; measure key→echo. The #3 focusable-input
   invariant, *inside* the modal.
3. **Result-marshalling correctness** — add a filter / change grouping, Save,
   assert the host applied it (status line "View: …" / re-group); Esc/Cancel yields
   no change. Help returns nothing; F3 returns a `ViewSettings` — this is the new
   axis vs #404.
4. **Modal-stacking** — F1 over the open F3 modal opens Help (native marker in B),
   Esc returns to the F3 modal.
5. **Post-modal list-nav latency** — after a full open/marshal/close cycle, a Down
   still redraws within budget (the #3 outer-responsiveness invariant), A vs B.

Functional PASS + printed numbers with a generous ceiling (a stable guard, not a
flaky micro-benchmark), like #404.

## Deliverable

- Flag-gated native FSG prototype (`FilterSortGroupFormBuilder`, the
  `NativeModalSpike.RunFilterSortGroupDialog` branch, the `OpenViewSettings` wiring).
- `fsg_modal_check.py` — the focusable A/B instrument + a regression guard.
- Unit test for the extracted pure result-build (`FilterSortGroupForm`).
- `docs/plans/native-modals-spike.md` updated with the focusable A/B grid and a
  **go/no-go** on migrating the #402 form modals.

## Out of scope

- Migrating the transient-modal category itself — that is the #402 decision this
  data feeds.
- The `windows`/`dotnet` drivers (the harness is ANSI-only per CLAUDE.md) — noted
  as still-unverified.
