# Dispatch provider model + multi-provider config migration (#497)

Part of the Super-Agent-chat epic (#491), but **independently valuable and not
spike-gated**: generalize agent dispatch from *one hard-wired `claude`
executable* to a **list of configured providers** with a chosen default.

## Problem / current state

`AgentDispatchSettings` (`Configuration/AgentDispatchSettings.cs`) exposes exactly
two "which agent" knobs — `ClaudeExecutable` (default `"claude"`) and `ExtraArgs`
— and `ToLauncherOptions()` projects those onto `TerminalLauncherOptions`, the
single seam both execution flows (`TerminalLauncher`, `BackgroundAgentRunner`) and
the planner consume. There is no notion of alternative agents.

Everything downstream of `ToLauncherOptions()` reads the projected
`TerminalLauncherOptions.ClaudeExecutable`/`ExtraArgs`, so **the projection is the
only seam that has to change** — the launcher/runner/planner are untouched.

## Design

### `DispatchProvider` (new domain type, `Configuration/DispatchProvider.cs`)

```csharp
public enum DispatchProviderKind { LocalCli }   // room for a future non-local kind

public sealed class DispatchProvider
{
    public string Name { get; set; } = "";                       // display name / selector key
    public string Executable { get; set; } = "claude";           // looked up on PATH
    public List<string> ExtraArgs { get; set; } = [];            // inserted before the prompt arg
    public DispatchProviderKind Kind { get; set; } = DispatchProviderKind.LocalCli;
}
```

The `Kind` discriminator is present now with a single `LocalCli` member so a
future hosted/API provider kind slots in without a schema change (the epic's
eventual "non-local" providers).

### `AgentDispatchSettings` changes

- Add `List<DispatchProvider> Providers` and `string DefaultProviderName` — the
  new **source of truth**.
- Convert `ClaudeExecutable`/`ExtraArgs` into **deserialize-only legacy shims**
  (`LegacyClaudeExecutable`/`LegacyExtraArgs`, `[JsonPropertyName("claudeExecutable"|"extraArgs")]`,
  `[JsonIgnore(WhenWritingNull)]`, nullable), exactly mirroring the established
  `LegacyPromptPreamble` (#100) / `LegacyExcludedStatuses` (#69) idiom.
- `ResolveDefaultProvider()` — the default provider by name, else the first
  configured, else a synthesized `{ "Claude", "claude", [] }` when the list is
  empty (so a hand-`new`'d settings object, and any un-migrated path, still
  dispatch `claude` with zero config).
- `ToLauncherOptions()` projects from `ResolveDefaultProvider()`, keeping the
  existing coalesce-blank-to-`claude` + trim + drop-blank-args cleaning.
- `IsDefault` treats an empty list **or** a single built-in-default provider
  (`claude`/blank exe, no args) as default, so the zero-config invariant and the
  `Apply_NullAgentDispatch_CoalescedToDefaults` test still hold after migration
  seeds a provider.

### Config migration (v6, `ConfigMigrations`)

Bump `CurrentVersion` 5 → 6. `MigrateDispatchProviders`:

- If `Providers` is already non-empty, no-op (a hand-authored / future config).
- Otherwise fold the legacy exe/args into a **single** provider named `"Claude"`,
  set it as `DefaultProviderName`, `Kind = LocalCli`. A blank/absent legacy
  executable coalesces to `"claude"`; legacy args are trimmed and blank-dropped.
- Null the legacy shims **regardless of version** (like `promptPreamble`) so a
  stray hand-added key is dropped, never re-persisted.

**Byte-identical guarantee:** every existing config wrote `claudeExecutable`
(default `"claude"`) and `extraArgs`, so migration always produces a provider
equal to the old pair → `ToLauncherOptions()` yields the identical
`TerminalLauncherOptions` → the launch command is unchanged. A fresh install
seeds `{ "Claude", "claude", [] }`.

### Validation (pure, unit-tested)

`DispatchProviderListEditor` (Phase 2, pure) owns list operations + validation:
non-blank/unique display names (dedup with a numeric suffix), blank executable
→ `claude`, args parse/format reusing `SettingsForm.ParseExtraArgs`, and
default-name resolution when a provider is removed/renamed.

### UI (Phase 2)

The F2 Dispatch section's single exe/args fields are replaced by an **"Edit
dispatch providers…"** button that opens a dedicated `DispatchProvidersScreen`,
mirroring the prompt-template editor pattern (`EditPromptTemplateRequested` →
`PromptTemplateEditorScreen`, pure logic in `PromptTemplateEditor`). The screen
is a thin Terminal.Gui shell over the pure `DispatchProviderListEditor`; wired in
both hosts (`TodoApp`, `SingleTaskApp`). The provider list is a sectioned
`ListView` — no second focusable pane on the main list (#3). Inline Y/N confirm
for delete (no nested modal, #38).

## Phases

1. **Model + migration + projection + `IsDefault`**, with unit tests
   (`DispatchProviderTests`, migration cases in `ConfigMigrationsTests`, projection
   in `AgentDispatchSettingsTests`). F2 keeps building green by editing the
   resolved default provider's exe/args (no UX change yet). Independently
   mergeable.
2. **Provider-list editor:** pure `DispatchProviderListEditor` (fully unit-tested)
   + thin `DispatchProvidersScreen` + host wiring + F2 button. `tui-validate`
   over the settings surface.
3. Finalize, draft PR, review subagent, address comments.

## Test plan

- Unit: `DispatchProvider` projection/coalesce/trim; migration from legacy
  exe/args (present / blank / absent) → single provider, byte-identical
  `ToLauncherOptions`; version-gating + idempotency; legacy keys stop persisting;
  `IsDefault` after migration; `DispatchProviderListEditor` add/rename/edit/delete/
  set-default/dedup/validate.
- Integration: none (no ClickUp boundary).
- `tui-validate`: the F2 Dispatch section + the providers editor screen; the
  existing settings A/B render check stays green.

## Acceptance criteria (from #497)

- An existing config migrates silently and dispatches exactly as before. ✔ (v6)
- Two or more local providers can be configured, and a default chosen. ✔ (Phase 2)
- Migration, projection and validation are unit-tested; `dotnet test` green, then
  `tui-validate` for the settings surface. ✔

## Scope boundary

If the provider-editor screen (Phase 2) proves too large to land cleanly in one
session, Phase 1 ships alone (the full tested model + migration + projection, with
the default provider still editable in F2) and the multi-provider **editor UI** is
deferred to a filed follow-up issue, noted in the PR.
