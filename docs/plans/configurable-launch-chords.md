# Configurable launch chords (#506, split-pane epic #502 slice D)

A config override layer over the `Keybindings` table for the **two launch
gestures** — `Ctrl+Enter` (new tab, `OpenInNewTab`) and `Ctrl+Alt+Enter` (split
pane, `OpenInSplitPane`) — so a user can rebind them (the enabler for unbinding
Windows Terminal's `Terminal.ToggleFullscreen` on `Alt+Enter` and pointing the
split gesture there — see #502). **Only these two actions** are made rebindable;
a general keybinding editor is explicitly a separate decision (#506 non-goal).

## Acceptance criteria (from #506)

- Config-supplied `(action) → token` overrides resolved **ahead of** the static
  defaults, at the one seam both the dispatcher and the footer agree through, so
  the footer/help pick up the configured chord automatically (the #355 property).
- Persisted in `AppConfig` (app-wide gestures, not `AgentDispatchSettings`),
  following the existing config + migration conventions.
- **Save-time validation:** reject an override that fails `Key.TryParse` or
  collides with another binding in the same context, naming the conflict, and
  keep the default.
- **Load-time defense:** an invalid persisted override falls back to the default
  binding rather than crashing the app.
- F10 Settings fields for the two chords, validated on save with an inline error.
- Tests: override-ahead-of-defaults; invalid/colliding rejected at save with the
  default retained; invalid persisted config surviving load; footer/dispatcher
  agreement under an override; the existing `Keybindings`/`HelpItemSets`
  cross-check suites still green with no override set.

## Verified current state (what the seam actually looks like)

- `Keybindings.Token(context, action)` (`Tui/Screens/Keybindings.cs:281`) is the
  static default lookup. The **dispatcher** resolves through it
  (`KeybindingDispatcher.On`, `Tui/KeybindingDispatcher.cs:35`). The **footer**
  does **not** — `HelpItemSets` is hand-authored literal `HelpItem`s; the
  footer↔table agreement is enforced by a **test** (`KeybindingsTests`
  `Footer_ShowsTheTableKey_ForEveryBinding`, which compares the footer literals
  against `Keybindings.All`). So "footer resolves through Token" is really
  "footer is cross-checked against the default table."
- The two launch actions are bound in the table **only** in `MainList` and
  `QuickOpen` (`Keybindings.cs:130/136/227/228`). `AllBindingsOfAnAction_ShareOneKey`
  pins each to **one token app-wide**, so the override is **per-action**, not
  per-context.
- **Task Detail is a pre-existing exception:** its `Ctrl+Enter` / `Ctrl+Alt+Enter`
  gestures are **hardcoded** in `TaskDetailScreen.OnKey` (`:1633/:1650`) by raw
  `KeyCode` match and are **not** in the table; the `DetailWithTaskTree` footer
  shows them as literals. Routing those through the override means first migrating
  them into the table/dispatcher — out of scope for "an override layer over the
  table." **Deferred** (see *Deferred*), so Detail dispatch **and** its footer
  both keep `Ctrl+Enter`/`Ctrl+Alt+Enter` and stay in agreement with each other.
- `AppConfig` is the app-wide config store (`Configuration/AppConfig.cs`);
  `ConfigMigrations` coalesces a hand-edited `null` object back to defaults
  (the `SuperAgents` precedent, `:154`). `SettingsForm` (`Tui/Screens/SettingsForm.cs`)
  is the pure, unit-tested input layer behind the Terminal.Gui `SettingsScreen`.

## Design — the override as a value, not static state

`Keybindings.All` / the default `Map` stay **pure defaults** (untouched), so the
`#355` cross-check keeps proving the hand-written footer matches the *default*
table — its actual job. Overrides are an explicit **immutable value** threaded to
the two consumers, so both apply the **same rule** and can't drift, and nothing
mutates shared static state (safe under xUnit's parallel tests):

- `LaunchChordOverrides` (new, pure) — built from config via
  `FromConfig(LaunchChordSettings)`, which parses each of the two tokens and
  **drops any that fail `Key.TryParse` (load-time defense)**; holds the effective
  override token (or null ⇒ default) per launch action. `Resolve(context, action)`
  returns the override token when `action` is a launch action the table binds in
  `context`, else null. Static `None`.
- `Keybindings` gains override-aware **overloads** `Token(context, action, overrides)`
  / `TryToken(...)` (parameterless versions unchanged) plus a single shared
  `ResolveLaunchToken` helper both the dispatcher and footer call, so there is one
  resolution rule.
- `KeybindingDispatcher` takes an optional `LaunchChordOverrides` (default `None`);
  `On` resolves override-then-default. Existing zero-arg construction is unchanged.
- `HelpItemSets.WithConfiguredLaunchChords(set, overrides)` — a render-time
  transform (mirroring the existing `WithContextualNewLabel`) that relabels the
  two launch items' display key + `Chord` to the configured token. Applied to the
  `MainList` and `QuickOpen` footers only (the table-driven ones).

**Validation** (`SettingsForm.ValidateLaunchChord(action, proposedToken, current)`,
pure): reject non-parseable (`Key.TryParse`) and reject a proposed token whose
parsed `KeyCode` equals any other live binding's `KeyCode` in **every context that
binds the action** (`MainList`, `QuickOpen`), computed with the sibling launch
action's *effective* (override-aware) token — so rebinding new-tab onto split-pane's
chord is caught. Returns Ok or an error naming the conflict; the caller keeps the
default on error.

## Phases

### Phase 1 — the seam (pure, fully CI-verifiable)

1. `LaunchChordSettings { string? NewTab; string? SplitPane; }` on `AppConfig`
   (camelCase `launchChords`); `ConfigMigrations` null-coalesces a hand-edited
   `"launchChords": null` back to defaults (SuperAgents precedent). Absent ⇒
   both null ⇒ defaults, no schema bump.
2. `LaunchChordOverrides` (parse/validate/drop-invalid `FromConfig`, `Resolve`,
   `None`).
3. `Keybindings` override-aware `Token`/`TryToken` overloads + `ResolveLaunchToken`.
4. `SettingsForm.ValidateLaunchChord` (parse + cross-context collision).
5. `HelpItemSets.WithConfiguredLaunchChords`.
6. `KeybindingDispatcher` optional `overrides` param.
7. Unit tests for all of the above (see *Tests*).

### Phase 2 — wire it end-to-end (works via `config.json`)

- `TodoApp.BuildListKeyDispatcher` passes `LaunchChordOverrides.FromConfig(_config.LaunchChords)`;
  the MainList footer render applies `WithConfiguredLaunchChords`.
- `QuickOpenScreen` takes an optional `LaunchChordOverrides` (ctor param), passes
  it to its dispatcher, and applies the footer transform to its `HelpItems`;
  instantiation sites pass the config overrides.
- After this, a hand-edited `launchChords` in `config.json` rebinds the two
  gestures and **both** the dispatch and the footer follow, on the list and
  quick-open.

### Phase 3 — F10 Settings fields *(deferred; tracked on #506)*

Shipped Phases 1–2 make the feature fully usable via a hand-edited `config.json`
(the config-first cadence this repo uses throughout), with the save-time
`ValidateLaunchChord` seam already in place and unit-tested. The F10 surface is a
discoverability enhancement, deferred as its own reviewed slice:

- Two fields in `SettingsScreen` for the chords, seeded from config, validated on
  save via the shipped `SettingsForm.ValidateLaunchChord` (inline error, default
  retained on invalid — a new *blocking-save-on-invalid* interaction for that
  always-closes-on-Save form), persisted to `AppConfig.LaunchChords`, then
  `_launchChords` recomputed + the list dispatcher rebuilt + footer refreshed on
  save (the `_agent` precedent).
- `tui-validate` leg: the Settings surface shows/accepts a chord, an invalid entry
  is rejected inline, and a rebound chord dispatches (footer reflects it).

## Tests

- **`LaunchChordOverrides`**: `Resolve` returns the override ahead of the default
  for a bound launch action / null for a non-launch action / null for a context
  that doesn't bind it; `FromConfig` drops an unparseable token (load-time defense)
  and keeps a valid one; `None` resolves to null everywhere.
- **`Keybindings` overload**: `Token(context, action, overrides)` returns the
  override for the two launch actions in `MainList`/`QuickOpen` and the default for
  every other pair; parameterless `Token` unchanged.
- **`SettingsForm.ValidateLaunchChord`**: Ok for a free parseable chord; error for
  an unparseable token; error for a token colliding with another `MainList`/`QuickOpen`
  binding; error for rebinding one launch action onto the other's effective chord;
  the message names the conflict.
- **`HelpItemSets.WithConfiguredLaunchChords`**: the two launch items show the
  configured token (key + `ActionKey`); a no-override set is returned unchanged;
  agreement — the footer's launch `ActionKey` equals `Token(context, action, overrides)`.
- **`KeybindingDispatcher`**: a dispatcher built with an override fires the handler
  for the new chord and **not** for the old default; zero-override construction is
  unchanged.
- **Regression**: the existing `Keybindings`/`HelpItemSets` cross-check suites stay
  green with no override set (they read defaults only).

## Deferred (kept tracked; new follow-up issue linked from the PR)

- **Task Detail's hardcoded `Ctrl+Enter`/`Ctrl+Alt+Enter` gestures.** They bypass
  the table (`TaskDetailScreen.OnKey`), so honoring the override there first needs
  them migrated into the table/dispatcher — a separate refactor. Until then Detail
  keeps the default chords (dispatch and footer consistent with each other).
- **The key-probe diagnostic** ("press a chord, see what the app receives") and the
  **WT `alt+enter` unbind hint** in the help screen — the issue floats both as a
  *"consider"*; polish, not the seam.
