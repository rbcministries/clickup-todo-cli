# Storage abstraction seam — `IStateStore` (issue #120)

## Goal

Introduce a **single persistence seam** so settings, focus pins, and (future)
cached payloads route through one backend-agnostic interface, decoupling call
sites from the on-disk format. This unblocks the rest of Epic #118 (#121 migrate
settings, #122 task cache, #123 feed cache, #124 TTL/eviction, #125 status/color
cache) and #155 (assignee frequency cache) — all of which need a place to persist
that isn't hand-rolled per feature.

The storage-backend decision landed in #119 (**LiteDB**). This issue does **not**
adopt LiteDB — it introduces the seam and reimplements the *current* file-backed
persistence behind it, byte-for-byte. LiteDB drops in later (its own issue) by
swapping one line at the composition root.

This is a **behaviour-preserving refactor**: `config.json` keeps the same path,
name, JSON shape (camelCase, indented, enums-as-strings), and migration on load.

## Design

`src/ClickUpTodo/Configuration/`:

### `IStateStore` (new)
A generic document-oriented persistence seam — the smallest surface that a
key/collection backend (LiteDB) and a file backend both satisfy cleanly:

```csharp
public interface IStateStore
{
    bool Exists(string key);
    T? Load<T>(string key) where T : class;   // null when absent
    void Save<T>(string key, T value) where T : class;
    void Delete(string key);
}
```

- **Settings** → `Save/Load<AppConfig>(StateKeys.Config)`.
- **Focus pins** → ride inside `AppConfig.PinnedTaskIds` (as today), so they
  persist via the same `Config` document — no new storage location (that would
  be a behaviour change; deferred).
- **Cached payloads** (tasks/feed/statuses/colors, #122/#123/#125) → their own
  keys via the same generic `Save/Load<T>`. No cache is persisted *yet*; the
  seam simply makes room for them without churning call sites.

### `StateKeys` (new)
`public const string Config = "config";` — maps to `config.json` in the file
backend, so the on-disk filename is unchanged. Cache issues add their own keys.

### `JsonFileStateStore : IStateStore` (new)
File-backed implementation — the reimplementation of today's `ConfigStore` I/O:
- `key` → `{key}.json` under the per-user data directory
  (`%APPDATA%\clickup-todo` / `~/.config/clickup-todo`).
- Reuses the exact `JsonSerializerOptions` `ConfigStore` used (`WriteIndented`,
  `CamelCase`, `JsonStringEnumConverter`) so `config.json` is byte-identical.
- Owns the default-directory resolution (moved down from `ConfigStore`); exposes
  `DirectoryPath` and `PathFor(key)` for file-specific callers (messaging/tests).
- `DefaultDirectory()` static; `ConfigStore.DefaultDirectory()` delegates to it
  so `TokenStore`/`OAuthAppCredentialStore` (out of scope) are untouched.

### `ConfigStore` (refactor → typed accessor over the seam)
Keeps its exact public surface (so the ~7 test files and production call sites
are untouched) but delegates all raw I/O to `IStateStore`:
- `ConfigStore(IStateStore store)` — primary ctor (composition root / DI).
- `ConfigStore(string? directoryPath = null)` — back-compat: builds a
  `JsonFileStateStore(directoryPath)`. Preserves `new ConfigStore(_dir)` in tests.
- `Load()` → `_store.Load<AppConfig>(Config) ?? new()` then `ConfigMigrations.Apply`
  (migration stays here — it's a config concern, not a storage one).
- `Save(config)` → `_store.Save(Config, config)`.
- `Exists()` → `_store.Exists(Config)`.
- `Delete()` (new) → `_store.Delete(Config)` — backend-agnostic replacement for
  `File.Delete(configStore.ConfigPath)` in `--reset`.
- `ConfigPath` / `DirectoryPath` — delegate to the `JsonFileStateStore` when the
  backend is file-based (file-specific info; kept for `--reset` fallback,
  SetupWizard messaging, and the raw-file migration tests).

### `LocalFocusStore` (unchanged signature)
Still `(AppConfig, ConfigStore)`. Because `ConfigStore` now sits on `IStateStore`,
`ToggleAsync`'s `store.Save(config)` already flows **through the seam** — pins are
a config-embedded concern, so persisting them = persisting the config document.
No signature churn, `LocalFocusStoreTests` untouched.

## Composition root (`Program.cs`)

The one place the backend is chosen — the drop-in point for LiteDB later:
```csharp
IStateStore stateStore = new JsonFileStateStore();
var configStore = new ConfigStore(stateStore);
```
`--reset` uses `configStore.Delete()` (was `File.Delete(configStore.ConfigPath)`)
— deletes the same `config.json` under the file backend, but backend-agnostically.

No change to `TodoApp` (still takes `ConfigStore`), `SetupWizard`
(`configStore.Save` + `ConfigPath` messaging), or the four `_configStore.Save`
sites in `TodoApp`. All flow through the seam transitively.

## Tests (`StateStoreTests`, xUnit, temp dir — mirrors `ConfigStoreTests`)

`JsonFileStateStore`:
- Round-trips a POCO through `Save<T>`/`Load<T>`; `Load` of an absent key → null;
  `Exists` false→true→(Delete)→false.
- `Config` key maps to `config.json` (`PathFor`), directory created on save.
- Serializes camelCase + indented + enum-as-readable-string (asserts on raw JSON,
  matching the existing `ConfigStore` guarantees).
- Independent keys coexist (proves cache payloads sit beside config without
  clobbering) — write `config` and a second key, both reload intact.

Backend-agnosticism (the drop-in criterion, concretely):
- An in-memory `IStateStore` test double injected into `ConfigStore` round-trips
  an `AppConfig` (with pins + agent block) with **no disk I/O** — proving call
  sites are decoupled from the file format.

Regression safety net (already present, must stay green): `ConfigStoreTests`,
`ConfigMigrationsTests`, `LocalFocusStoreTests`, `AuthModeConfigTests`,
`DetailViewSettingsTests`, `BadgeDisplayTests`, `ViewSettingsConfigTests` — all
construct `new ConfigStore(_dir)` and read raw `config.json`; they now exercise
the seam transitively and must pass unchanged (no test weakened/deleted).

## Non-goals / deferred

- **Adopting LiteDB** — the actual backend swap is a follow-up (the seam makes it
  a one-line change at the composition root).
- **Persisting caches** — task/feed/status/color caches land in #122/#123/#125;
  this only provides the seam they'll use.
- **Moving pins to their own collection** — would change the on-disk location
  (behaviour change); pins stay in `config.json` for now.
- **`TokenStore`** — explicitly out of scope; token stays in `token.bin` with its
  own DPAPI/obfuscation path.
