# Migrate settings persistence to LiteDB (issue #121)

Part of Epic #118. Depends on: #119 (ADR — **verdict: LiteDB**, closed) and
#120 (the `IStateStore` seam, merged). Both are done, so this is the backend
adoption the seam was built for: *"the actual backend swap is a follow-up …
a one-line change at the composition root"* (`storage-abstraction-seam.md`).

## Goal

Move the app's settings document (`AppConfig` — workspace/list ids,
`RefreshSeconds`, `PinnedTaskIds`, `ViewSettings`, `AgentDispatch`,
`SchemaVersion`) off the hand-rolled `config.json` file and onto the chosen
backend (LiteDB) behind the existing `IStateStore` seam, with a one-time,
idempotent import of any existing `config.json` so upgrades are seamless and
downgrade stays possible.

## Design

### `LiteDbStateStore : IStateStore, IDisposable` (new)
`src/ClickUpTodo/Configuration/LiteDbStateStore.cs`

- Backed by a single LiteDB file `state.db` in the per-user data directory
  (`%APPDATA%\clickup-todo` / `~/.config/clickup-todo`) — same directory as
  `config.json` and `token.bin`.
- One collection, `state`, of documents `{ _id: <key>, json: <string> }`. The
  value is stored as a **System.Text.Json string** using the *exact same*
  serializer options as `JsonFileStateStore` (camelCase, indented, enums as
  strings). This keeps serialization semantics byte-for-byte identical to the
  file backend — `ConfigMigrations`, enum handling, and null-coalescing all
  behave exactly as before — and makes the two backends trivially
  interchangeable. (LiteDB's own BSON mapper is deliberately *not* used for the
  payload, to avoid a second, divergent serialization contract.)
- Shared serializer options are extracted into `StateJson.Options` (internal
  static) and consumed by *both* stores, so `config.json` stays byte-identical
  and the LiteDB payload matches it.
- `Connection=shared` so a stray second process can't hard-lock the file.
- `IDisposable` — holds the `LiteDatabase` for the store's lifetime (settings
  writes are rare; cache writes in #122+ will reuse the open handle). Disposed
  at the composition root on exit.
- Static `DefaultDatabasePath()` → `<data-dir>/state.db`.

### `SettingsMigration` (new)
`src/ClickUpTodo/Configuration/SettingsMigration.cs`

`ImportLegacyConfig(IStateStore target, JsonFileStateStore legacy) : bool`

- **No-op if `target.Exists(Config)`** — already migrated (idempotent; safe to
  run every launch).
- **No-op if the legacy store has no `Config`** — fresh install, nothing to
  import.
- Otherwise `target.Save(Config, legacy.Load<AppConfig>(Config))` and return
  `true`. Copies the settings document as-stored (schema version preserved;
  `ConfigMigrations` still runs later on `ConfigStore.Load`).
- **Leaves the legacy `config.json` in place** so a downgrade to the JSON
  backend still finds its settings.

### Composition root (`Program.cs`)
The single drop-in point the seam promised:

```csharp
var dataDir = JsonFileStateStore.DefaultDirectory();
var liteStore = new LiteDbStateStore(Path.Combine(dataDir, "state.db"));
SettingsMigration.ImportLegacyConfig(liteStore, new JsonFileStateStore(dataDir));
IStateStore stateStore = liteStore;
var configStore = new ConfigStore(stateStore);
```

`liteStore` is disposed on exit (`using`). Everything else (`ConfigStore`,
`--reset` via `configStore.Delete()`, `SetupWizard`, `TodoApp`,
`LocalFocusStore`) is unchanged — it all flows through `IStateStore`.

`ConfigStore.ConfigPath` / `DirectoryPath` (file-specific accessors used by
SetupWizard messaging and `--reset` fallback) return `string.Empty` under a
non-file backend today; `--reset` already uses the backend-agnostic
`configStore.Delete()`, so no file-path dependency breaks. (The legacy
`config.json` is intentionally *not* deleted by `--reset` since the live backend
is now LiteDB; documented as acceptable — see "Deferred" below.)

## Tests (`tests/ClickUpTodo.Tests/`)

New `LiteDbStateStoreTests` (temp file, `IDisposable` cleanup — mirrors
`StateStoreTests`):
- Round-trips a POCO and an `AppConfig` through `Save/Load`.
- `Load` of an absent key → null; `Exists` false→true→(Delete)→false.
- Independent keys coexist without clobbering (proves cache payloads sit beside
  config — #122/#123).
- Persists across store instances over the same file (close + reopen).
- Value stored is camelCase/enum-as-string (asserts the payload matches the
  file backend's contract).
- `ConfigStore` over a `LiteDbStateStore` round-trips (the drop-in criterion,
  concretely) — pins + agent block survive.

New `SettingsMigrationTests`:
- **Fresh install** (no legacy file): import is a no-op, returns false;
  `ConfigStore.Load()` yields unconfigured defaults.
- **Upgrade** (legacy `config.json` present): import copies settings; a
  `ConfigStore` over the target now loads identical effective settings; returns
  true.
- **Idempotent**: a second import is a no-op (returns false) and does not
  clobber a value that changed in the target after the first import.
- **Legacy file left in place** after import (downgrade path intact).

All existing persistence tests (`StateStoreTests`, `ConfigStoreTests`,
`ConfigMigrationsTests`, `LocalFocusStoreTests`, …) stay green unchanged — they
run over `JsonFileStateStore` / in-memory doubles, which are untouched.

## Non-goals / deferred

- **Moving caches or pins into their own LiteDB collections** — pins still ride
  in the `Config` document; task/feed/status caches land in #122/#123/#125 and
  will add their own `StateKeys`.
- **Archiving/removing the legacy `config.json`** — left in place for downgrade;
  a later cleanup pass can archive it if desired.
- **Token storage** — `token.bin` stays on its own DPAPI/plaintext path (out of
  scope per #119/#120).

## Validation

`dotnet build -c Release` (0 warn/0 err) + `dotnet test -c Release` green. Not a
rendering/keypress/list-source change, so `tui-validate` is not required; a
manual launch smoke-check (settings load + a settings change persists across
restart) is described in the PR.
