# A4 — Consolidated F2 "Dispatch" settings: user-configurable defaults (#101)

Part of epic #90. Depends only on #91 (config wiring), which is **closed**. Foundational:
the Dispatch-pane controls (#93/#94/#95/#97) initialize from the defaults this issue
persists, so the defaults must exist first.

## What #101 asks for

Every toggle/control the Dispatch epic introduces should have a **user-configurable
default**, exposed in a dedicated **"Dispatch"** section on the F2 settings screen, plus a
button to open the prompt-template editor (#100). When the user opens the Dispatch pane on
a task (#93), each control is pre-set from these saved defaults.

## Scope this session (a complete, verifiable slice)

The config layer + F2 section are self-contained and touch **no files the two open PRs
touch** (#139 = detail-view dispatch pane; #137 = detail Other tab). They live in
`Configuration/AgentDispatchSettings.cs`, `Tui/Screens/SettingsScreen.cs`, and the tests.

### 1. New persisted defaults on `AgentDispatchSettings`

- `enum AgentSessionMode { Interactive, OneOff }` (new, in `AgentDispatchSettings.cs`
  alongside `AgentWorkingDirectory`). This is the **default** the #94 per-dispatch toggle
  reads (`claude` vs `claude -p`).
- `AgentSessionMode DefaultSessionMode = AgentSessionMode.Interactive` — default preserves
  today's interactive behaviour.
- `bool DefaultPostResultsToComments = false` — the **default** the #97 per-dispatch
  "Post results to Comments" toggle reads. Off by default.
- Fold both into `IsDefault` so a config that only carries these defaults still reads as
  zero-config.

Backward-compatible: new properties with defaults, so an old `config.json` missing the
keys deserializes to Interactive / off (mirrors the `AgentDispatch = new()` and
`DefaultWorkingDirectory` blank-sentinel patterns). Enums persist as readable strings via
the existing `JsonStringEnumConverter`.

### 2. Working-directory precedence helper (pure, unit-tested)

The issue asks for an explicit precedence: **per-task cache hit (#96) → else the
configured default mode (existing `WorkingDirectory` + `#92` base dir / `#98` task-derived
candidate)**. Add:

```
string? ResolveEffectiveWorkingDirectory(string? cachedDirectory,
                                         string? taskDerivedDirectory,
                                         string? homeDirectory)
    => cachedDirectory (if non-blank) ?? ResolveWorkingDirectory(taskDerivedDirectory, homeDirectory);
```

This layers the #96 cache on top of the existing mode resolver. #96 supplies
`cachedDirectory` when it lands; today all call sites pass `null`, so behaviour is
unchanged. The existing `WorkingDirectory` mode ({TaskDerived, Home, Fixed}) already *is*
the "default working-directory behaviour" the issue references — no new mode field needed.

### 3. F2 "Dispatch" section

In `SettingsScreen`:
- Rename the right-column header `─ Agent dispatch (A) ─` → `─ Dispatch ─` (drops the bare
  `A`, which #93/#139 is moving to `Ctrl+A`; a neutral label avoids referencing a key in
  flux and matches the issue's "dedicated 'Dispatch' section").
- Add two cycle/toggle buttons (same pattern as the existing terminal/working-dir cycle
  buttons): **Default session** (Interactive ↔ One-off) and **Default post to Comments**
  (Off ↔ On).
- Thread both new fields through the `SettingsResult`'s `AgentDispatchSettings`
  construction in the Save handler (otherwise they'd reset to defaults on save).

## Deferred (tracked, not this session)

- **Prompt-template editor button (#100).** The editor *screen* is built in #100; #101
  only guarantees its entry point lives in this section. With no editor screen to navigate
  to yet, the existing prompt-preamble field stays in place and the button lands with #100.
  Noted in the PR; #100 already tracks it.

## Tests

- `AgentDispatchSettingsTests`: defaults (Interactive, post-off, still `IsDefault`);
  `IsDefault` false once `DefaultSessionMode`/`DefaultPostResultsToComments` are
  customised; `ResolveEffectiveWorkingDirectory` — cache hit wins, blank cache falls
  through to the mode resolver, across the three modes.
- `ConfigStoreTests`: round-trip of the two new fields; enums persist as readable strings
  (`Interactive`/`OneOff`, not ordinals); an old config missing the keys loads to the
  defaults.

## Verification

- `dotnet build -c Release` (0 warn / 0 err) + `dotnet test -c Release` (green; integration
  skipped without token) + `dotnet format`.
- TUI (build-verified per the repo rule): open F2, cycle the two new buttons, Save, confirm
  `config.json` shows `defaultSessionMode` / `defaultPostResultsToComments`; reopen shows
  the persisted values. No new focusable list pane — the two buttons sit in the existing
  single settings screen (the #3 latency model is untouched; this screen already has
  focusable controls).
