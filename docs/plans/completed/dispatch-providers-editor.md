# Multi-provider dispatch editor UI (#547)

Follow-up to #497 / PR #546, which shipped the `DispatchProvider` model +
byte-identical config migration (see `docs/plans/dispatch-provider-model.md`).
That slice left the **provider list** editable only by hand-editing
`config.json`; the F10 Dispatch section could still only edit the *resolved
default* provider's executable + extra args. This issue adds the dedicated
management UI so the whole list — add / rename / edit / delete / choose default —
is reachable from F10, mirroring the prompt-template-editor pattern.

## Current state (on `main`)

- `AgentDispatchSettings.Providers` (`List<DispatchProvider>`) + `DefaultProviderName`
  are the source of truth; `ToLauncherOptions()` projects from
  `ResolveDefaultProvider()`.
- `SettingsScreen` shows a single "Claude executable" + "Extra args" pair that
  edits the resolved default provider via the pure
  `SettingsForm.ApplyDefaultProviderEdit`, carrying the other providers through
  untouched.
- The prompt-template editor is the template to copy: `SettingsScreen` raises
  `EditPromptTemplateRequested` (a request record carrying the current value + an
  `Apply` callback); `TodoApp.OpenSettings` stacks a `PromptTemplateEditorScreen`
  over settings and folds the returned value back via the callback, so the
  settings screen's own Save is the transaction boundary.

Settings (F10) is only opened by `TodoApp`; `SingleTaskApp` has no settings
surface, so — like the existing prompt-template editor — the new editor is wired
only in `TodoApp`.

## Design

### Pure core — `DispatchProviderListEditor` (`Tui/Screens/DispatchProviderListEditor.cs`)

A small mutable editor over a working copy of the provider list + default name,
fully unit-tested (mirrors `SettingsForm` / `ChecklistArranger`). The screen is a
thin shell over it; all decisions live here.

- Constructor deep-copies the incoming providers; an **empty** list is seeded with
  a single built-in `{ "Claude", "claude" }` default so the editor always shows at
  least one row (mirrors `AgentDispatchSettings.ResolveDefaultProvider`). The
  default name resolves to the matching provider, else the first.
- `Add()` appends `{ UniqueName("New provider"), "claude", [] }` and returns its
  index. `SetName` (trim; blank → "Provider"; **dedup** with a ` (n)` suffix; the
  default follows a renamed default), `SetExecutable` (stored trimmed; blank kept,
  coalesced at `Build`), `SetExtraArgs` (trimmed, blanks dropped), `SetDefault`,
  and `Delete` (re-seeds a default when the list would empty; reassigns the
  default to the first row when the deleted row was default).
- `IsDefault(i)` drives the `●` marker. `Build()` returns a normalized,
  deep-copied `(Providers, DefaultProviderName)` — executables coalesced blank →
  `claude`, args cleaned, the default name coerced to an existing provider.

Names are exact `Ordinal` selector keys throughout, matching
`ResolveDefaultProvider` / `ApplyDefaultProviderEdit`.

### Screen — `DispatchProvidersScreen` (`Tui/Screens/DispatchProvidersScreen.cs`)

A full-window `Screen` (like `FilterSortGroupScreen`), **not** an overlay:

- A `ListView` of provider summary rows (`● Claude — claude`), the single
  focusable list — the master.
- `Name` / `Executable` / `Extra args` `TextField`s edit the selected provider —
  the detail. Field values are committed into the editor on selection change and
  on Save (a `_syncing` guard prevents feedback while loading a row).
- Buttons: **Add**, **Delete**, **Set default**, **Save**, **Cancel**. `Delete`
  arms an inline `Y/N` confirm on a status label (no nested modal, #38 — mirrors
  `PromptTemplateEditorScreen`'s reset confirm). `Delete`/`Backspace` on the list
  also arm it.
- `Save` exposes a `DispatchProvidersResult(Providers, DefaultProviderName)` via
  `Result`; Cancel/Esc leave it null.
- Multiple focusable controls on a dedicated screen is the established pattern
  (`SettingsScreen`, `NewTaskScreen`); the #3 single-focusable-pane rule is about
  the **main task list**, not sub-screens.

### F10 entry point — `SettingsScreen`

The single "Claude executable" / "Extra args" fields are **replaced** by an
`Edit dispatch providers…` button (the "replace" option #547 sanctions), plus a
read-only summary label (`Providers: Claude (default)` / `N providers …`). The
screen carries `_providers` + `_defaultProviderName` (deep-copied from the
incoming settings, exactly as it carries `_promptTemplate`); the button raises
`EditDispatchProvidersRequested`, and the returned list/default fold back through
the request's `Apply` callback. `BuildDispatchSettings` persists the carried
list/default directly. If the user never opens the editor the carried values are
byte-identical to the incoming ones, so a migrated config re-saves unchanged.

The now-orphaned `SettingsForm.ApplyDefaultProviderEdit` (the interim
single-field editor from #546) and its unit tests are retired with this change —
the list editor supersedes it (mirrors #560's retirement of `UsesTaskDerivedOutput`).

### Keybinding table + footer (#355)

`DispatchProvidersScreen` is a full-window screen, so it gets a
`ScreenContext.DispatchProviders` with the usual `Help = F1` / `Back = Esc`
bindings and a matching `HelpItemSets.DispatchProviders` footer set, kept in sync
by the existing `KeybindingsTests` / `HelpLineTests` cross-checks (their
hardcoded set/context lists are extended to include the new set).

## Phases

1. Pure `DispatchProviderListEditor` + unit tests (`DispatchProviderListEditorTests`).
2. `DispatchProvidersScreen` + `SettingsScreen` button/carry + `TodoApp` wiring +
   `ScreenContext`/`Keybindings`/`HelpItemSets` + cross-check test updates; retire
   `ApplyDefaultProviderEdit`. Build 0-warning, `dotnet test` green.
3. A mouse-driven `tui-validate` check (`dispatch_providers_check.py`) that opens
   F10 → the editor, adds/edits/sets-default/saves, and asserts the in-session
   round-trip; finalize the PR.

## Test plan

- Unit: editor add / rename+dedup / set-exe (blank→claude at Build) / set-args /
  delete (default reassignment, empty re-seed) / set-default / build normalization /
  `IsDefault`; the empty-list seed; default-name resolution.
- No integration tests (no ClickUp boundary).
- `tui-validate`: F10 → editor round-trip; the main-list A/B checks stay green
  (the settings sub-screen doesn't touch the list render path).

## Acceptance criteria (from #547)

- Two or more local providers can be added, renamed, edited and deleted, and a
  default chosen, entirely from F10 — persisted to `config.json` and picked up on
  the next dispatch. ✔
- Pure editor logic is unit-tested (dedup, delete-reassigns-default, validation);
  `dotnet test` green. ✔
- A `tui-validate` check drives F10 → the editor → add/edit/set-default/save and
  asserts the round-trip; the existing settings-surface checks stay green. ✔
