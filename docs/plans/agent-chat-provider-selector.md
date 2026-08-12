# Plan — Provider selector in the Dispatch pane (#498)

Slice **C** of the Agent-chat epic #491. Depends on **A** (framing, #496 — merged #573) and
**B** (provider model, #497 — shipped #546: `DispatchProvider`, `AgentDispatchSettings.Providers`
+ `DefaultProviderName`, `ResolveDefaultProvider()`, and the F10 editor). This slice lets the user
pick **which configured provider** a single dispatch targets, from the Dispatch pane, without
changing the persisted default — the exact shape of the #275 per-dispatch launch-location override,
generalized from a binary toggle to an N-way provider pick.

## Acceptance criteria (from the issue)

- A provider control in the pane, listing configured local providers (**B**).
- `DispatchRequest.Provider`; a persisted last-used default the pane initializes from.
- Extend `DispatchPaneModel` focus cycling and height for the new row.
- Generalize the `LaunchLocationApplies` idea into a per-provider "which options apply" predicate,
  pure and unit-tested, so a (future) agent provider greys out what it can't use.
- Default behaviour unchanged: a user who never opens the provider control dispatches exactly as
  today. Options that don't apply are visibly disabled and ignored downstream (the `LaunchLocation`
  contract).
- Pane routing/height/applicability logic unit-tested; `dotnet test` green, then `tui-validate`.

## Scope decision — local providers now; discovered agents deferred

The issue notes agent (Super-Agent) entries *"additionally depend on #490's C (agent directory)"*
— i.e. `AgentDirectoryCache` (#494), which is still blocked by the #492 spike (PR #578). Only the
**local-provider** half of the control is unblocked today, so this slice delivers exactly that: the
selector lists the configured `LocalCli` providers. The **discovered-Super-Agent** entries and their
"greyed-out working-dir / session-mode / launch-location" behaviour are deferred to when #494 lands,
tracked there. The applicability predicate is built now (keyed on `DispatchProviderKind`) so the
grey-out rule already exists for the non-local kind the moment such a provider can be selected — no
rework, just a new selectable kind.

## Design

The chosen provider changes the launched **executable + extra args**. Threading mirrors #275
(`LaunchLocation`) precisely: pane → `DispatchRequest.Provider` (a provider *name*) → the pure
`DispatchCoordinator.Plan` resolves it against `settings` → `ResolvedDispatch` carries the projected
exe+args → `RunInteractive`/`RunBackground` pass them to `AgentDispatcher.DispatchAsync` /
`DispatchBackgroundAsync`, which apply `options with { ClaudeExecutable = …, ExtraArgs = … }` for
that one launch. A `null`/blank `Provider` means **no override** — the dispatcher uses the options it
was constructed with (`settings.ToLauncherOptions()` = the default provider), so the default path is
provably byte-identical.

### Phase 1 — pure model + settings (unit-tested)

1. **`DispatchRequest`**: add `string? Provider = null` (last param, backward-compatible). Null/blank
   ⇒ dispatch the configured default provider, exactly as today.
2. **`AgentDispatchSettings`**:
   - `ResolveProvider(string? name)` — the named provider (Ordinal match), else
     `ResolveDefaultProvider()`. Never null. Mirrors `ResolveDefaultProvider`.
   - `ToLauncherOptions(DispatchProvider provider)` overload projecting a *given* provider; the
     existing no-arg `ToLauncherOptions()` becomes `ToLauncherOptions(ResolveDefaultProvider())`, so
     the executable/args cleaning (blank→`claude`, trim, drop-blank args) stays single-sourced.
   - `LastDispatchProviderName` (persisted, default `""`) — the remembered per-dispatch pick, distinct
     from the F10-configured `DefaultProviderName`. Absent in an old config ⇒ `""`; no migration.
     Excluded from `IsDefault`/zero-config accounting only if blank (it is by default).
3. **`DispatchPaneModel`** (pure helpers, unit-tested):
   - `DispatchOptionApplies(DispatchProviderKind kind, DispatchOption option)` — the generalized
     predicate. For `LocalCli`, all options apply (today's behaviour); the seam greys out
     WorkingDirectory / SessionMode / LaunchLocation for a future non-local kind. `LaunchLocationApplies`
     stays (it composes: launch location applies iff interactive **and** the kind allows it).
   - `InitialProviderIndex(count, lastUsedIndex, defaultIndex)` — which row the selector opens on:
     the last-used pick when valid, else the configured default, else 0. Pure index math so the
     seed rule is tested, not buried in glue.
   - `ProviderRowVisible(providerCount)` ⇒ `providerCount >= 2` — the control only appears when there
     is a real choice, keeping the zero-/single-provider pane byte-identical (see Phase 3).

### Phase 2 — coordinator + dispatcher threading (unit-tested)

4. **`DispatchCoordinator.ResolvedDispatch`**: add `string? ProviderExecutable` and
   `IReadOnlyList<string>? ProviderExtraArgs` (null ⇒ no override).
5. **`DispatchCoordinator.Plan`**: when `request.Provider` is non-blank, resolve
   `settings.ResolveProvider(request.Provider)` and project it via `settings.ToLauncherOptions(provider)`,
   carrying the cleaned `ClaudeExecutable`/`ExtraArgs` onto the plan; blank ⇒ null override.
6. **`AgentDispatcher.DispatchAsync`** / **`DispatchBackgroundAsync`**: add
   `string? providerExecutable = null, IReadOnlyList<string>? providerExtraArgs = null`. When the
   executable is non-blank, build a per-call `options with { ClaudeExecutable = …, ExtraArgs = … }`
   (both flows — a one-off Codex run must run `codex`); null leaves `_options` untouched, so every
   existing caller/test is unaffected.
7. **`RunInteractive`/`RunBackground`**: pass `plan.ProviderExecutable`/`plan.ProviderExtraArgs`.

### Phase 3 — TUI pane + host persistence (build-verified + tui-validate)

8. **`TaskDetailScreen`**: a new `providers` (+ `defaultProviderName` / `lastDispatchProviderName`)
   ctor param. When `ProviderRowVisible(providers.Count)`, add a **horizontal `RadioGroup`** ("Agent:")
   of provider display names as a new bottom row (bump the below-browser row count 2→3), seeded via
   `InitialProviderIndex`, added to `_dispatchControls` (Tab order) and `_promptBox`. With 0/1
   providers the pane is unchanged. `SubmitDispatch` reads the selected provider name into
   `DispatchRequest.Provider`. Height uses `PreferredHeightWithBrowser` with the bumped count.
9. **`TodoApp`/`SingleTaskApp`**: seed the screen with `_config.AgentDispatch.Providers`,
   `DefaultProviderName`, `LastDispatchProviderName`; on dispatch, when the pick differs from
   `LastDispatchProviderName`, update it and persist `config.json` (reusing the host's existing
   post-dispatch save seam, alongside the #96 working-dir cache reconcile).

## Tests

- `AgentDispatchSettingsTests`: `ResolveProvider` (named / blank→default / unknown→default);
  `ToLauncherOptions(provider)` projects the given provider; `LastDispatchProviderName` round-trips
  and doesn't disturb `IsDefault` when blank.
- `DispatchPaneModelTests`: `DispatchOptionApplies` (LocalCli→all true; a non-local kind→working-dir
  /session-mode/launch-location false); `InitialProviderIndex` (last-used valid / invalid→default /
  neither→0); `ProviderRowVisible` (0,1→false; 2+→true).
- `DispatchCoordinatorTests`: `Plan` carries the chosen provider's exe+args; a blank `Provider` ⇒
  null override (default path unchanged).
- `AgentDispatcherTests`: a fake launcher captures the `TerminalLauncherOptions`; assert an explicit
  provider override reaches the launcher (exe+args), and omitting it preserves the constructor
  `_options` (both interactive and background flows).
- `ConfigStore`/`ConfigMigrations` round-trip for `LastDispatchProviderName` (present + absent).

## Out of scope / deferred (tracked)

- **Discovered Super-Agent entries** in the selector and the non-local-provider grey-out behaviour —
  gated on the agent directory `AgentDirectoryCache` (**#494**), itself blocked by the #492 spike
  (PR #578). The applicability predicate and the `DispatchProviderKind` seam are built now so this is
  additive.
- No ClickUp API surface ⇒ no `clickup-openapi.json` change, no Kiota regen.
- No second focusable pane (#3) — the selector is another control on the existing single Dispatch
  pane. Bare letters stay reserved for type-ahead (#12) — the RadioGroup uses arrows.
