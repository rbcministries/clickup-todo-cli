# D4 — Cache the per-task Dispatch working directory across relaunches (#96)

Part of epic #90. Depends on **#95** (working-dir control — the field this pre-fills) and
**#91** (config wiring) — both **closed**, along with #92/#93/#94. All the seams this needs
already exist: `AgentDispatchSettings.ResolveEffectiveWorkingDirectory` has a documented
`cachedDirectory` slot "which #96 also seeds", `ShowPrompt` notes "#96 will later pre-fill
the field from a per-task cache", and `DispatchAgent`'s comment references the cache slot.

## What #96 asks for

Once a user picks an explicit working directory for dispatching from a given task, remember
it — keyed by task id — so subsequent dispatches from that same task pre-fill the field,
**including across app relaunches**. Persist in `AppConfig`, saved via `ConfigStore` to
`config.json` (same path as `PinnedTaskIds`/`View`). Only store explicit **non-default**
picks; a pick equal to the resolved default is not persisted (and clears any stale entry).
Backward-compatible: absent key → empty map; old configs load unchanged.

## Design decisions

- **Key by task id (`TaskDetail.Id`)**, not `custom_id` — task id is always present; custom
  id may be blank. (Issue's recommended choice.)
- **"Selecting the default clears the entry."** We store only explicit non-default picks,
  and when the user reverts to the default (blank field, or a pick equal to the resolved
  default mode dir) we *remove* the entry so pre-fill stays truthful on the next open. This
  is the issue's recommended "don't persist a value equal to the resolved default" plus the
  natural clear-on-revert corollary (the field is pre-filled from the cache, so a user who
  blanks it is deliberately choosing the default).

## Scope this session (complete, verifiable slice)

### 1. `AppConfig.TaskWorkingDirectories` (`Configuration/AppConfig.cs`)

`Dictionary<string, string> TaskWorkingDirectories { get; set; } = [];` — task id ⇒ absolute
working-directory path. New property with an empty-map default, so an old `config.json`
missing the key deserializes to empty (mirrors `PinnedTaskIds = []`). Persisted camelCase as
`taskWorkingDirectories` by the existing `ConfigStore` options.

### 2. `ConfigMigrations` null-coalesce (`Configuration/ConfigMigrations.cs`)

`config.TaskWorkingDirectories ??= new();` — mirror the existing `AgentDispatch ??= new()`
guard so a hand-edited `"taskWorkingDirectories": null` degrades to an empty map instead of
NRE'ing later call sites. No version bump (absent key already handled by the default).

### 3. Pure cache helper (`Configuration/DispatchWorkingDirectoryCache.cs`, new)

I/O-free, Terminal.Gui-free static class holding the read/write rules so they're unit-tested
independent of the TUI:

- `PreFill(IReadOnlyDictionary<string,string> cache, string taskId) → string` — the cached
  path if stored and non-blank, else `""` (field starts blank ⇒ default behaviour).
- `Update(IDictionary<string,string> cache, string taskId, string? chosenDirectory,
  string? resolvedDefault) → bool` — reconciles the cache after a dispatch. Stores an
  explicit non-default pick; removes the entry when the pick is blank or equals
  `resolvedDefault`. Mutates in place; returns whether it changed (so the caller only Saves
  when needed). Ordinal path comparison (Linux is case-sensitive), both sides trimmed.

### 4. Pre-fill wiring (`Tui/Screens/TaskDetailScreen.cs`)

- New optional ctor param `string? cachedWorkingDirectory = null`, stored in a
  `_initialWorkingDirectory` field.
- In `ShowPrompt`, seed `_workingDirField.Text = _initialWorkingDirectory ?? string.Empty`
  instead of always blank (browser still resets to root — pre-fill is independent of
  navigation). Update the stale "#96 will later pre-fill" comment.

### 5. Dispatch wiring (`Tui/TodoApp.cs`)

- `OpenDetail`: pass `cachedWorkingDirectory: DispatchWorkingDirectoryCache.PreFill(
  _config.TaskWorkingDirectories, detail.Id)` into the `TaskDetailScreen` ctor.
- `DispatchAgent`: after `chosenDir`/`baseDir` are resolved (UI thread, before the
  `Task.Run` hand-off), compute `resolvedDefault = settings.ResolveWorkingDirectory(baseDir,
  home)` and call `DispatchWorkingDirectoryCache.Update(_config.TaskWorkingDirectories,
  detail.Id, chosenDir, resolvedDefault)`; if it returns true, `_configStore.Save(_config)`.
  Cache the user's explicit choice regardless of launch success (they chose it).

## Tests

- **`DispatchWorkingDirectoryCacheTests`** (new, pure): `PreFill` hit / miss / blank-stored;
  `Update` stores explicit non-default; no-ops on an identical existing entry (returns
  false); removes on blank pick; removes on pick == resolvedDefault; removes a stale entry
  when reverting to default; leaves other tasks' entries untouched.
- **`ConfigStoreTests`**: round-trip of `TaskWorkingDirectories`; absent key → empty map;
  camelCase key in the JSON.
- **`ConfigMigrationsTests`**: explicit `null` map coalesces to empty.

## Deferred / out of scope

None — the issue is fully self-contained. `#T1`/`#98` (task-derived default when no cache)
already landed; pre-fill simply falls through to it when the map has no entry.

## Verification

- `dotnet build -c Release` (0 warn / 0 err), `dotnet test -c Release` (green; integration
  skips without `CLICKUP_TOKEN`), `dotnet format`.
- TUI: the only UI change is seeding a `TextField`'s initial text in the already-existing
  Dispatch pane — no new focusable pane, no key changes (the #3 single-ListView latency
  model and #12 type-ahead are untouched). Verify via `tui-validate` that render/latency are
  unperturbed, plus build + reasoning for the pre-fill path (CI can't drive F-key dispatch).
  Manual check: pick a dir in the Dispatch pane for a task, dispatch, reopen → field
  pre-filled; blank it + dispatch → entry cleared; confirm `config.json` shows
  `taskWorkingDirectories`.
