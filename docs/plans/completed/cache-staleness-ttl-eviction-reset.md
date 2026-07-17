# Cache staleness, TTL, eviction & reset (#124)

Part of Epic #118 (persistent local cache). Depends on the persistent task cache (#122)
and feed cache (#123), both merged. Keeps those caches correct and bounded now that they
exist.

## Current state (verified)

- `TaskCache` (`Services/TaskCache.cs`) and `FeedCache` (`Services/FeedCache.cs`) each
  persist **one** document via `IStateStore` (`StateKeys.Tasks` / `StateKeys.Feed`),
  superseded on every `Save`. Each doc carries a `SchemaVersion` and a context `Key`
  fingerprint (workspace / list / assignee scope, + `FeedShowCompleted` for the feed).
- **Schema versioning** is already implemented: a `SchemaVersion` mismatch on load is a
  clean miss (discard, never mis-read). Corrupt JSON is a miss, never a throw.
- **No capture timestamp** — nothing records *when* a snapshot was taken, so there is no
  staleness signal, no age-based eviction, and the instant-paint just says "refreshing…"
  with no indication of how old the painted data is.
- `#125`'s `StatusCache` / `ListColorCache` already carry per-entry capture timestamps +
  a TTL — the precedent to mirror.
- **Reset:** `--reset` / `--logout` (`Program.cs`) clears token, config (+ legacy),
  `TaskCache`, `FeedCache`, `Statuses`, `ListColors` — **but not** the assignee-frequency
  cache (`StateKeys.Assignees`). That's the one reset gap.
- There is **no runtime workspace-switch or re-auth path** (SettingsForm/SettingsScreen
  have no workspace/token controls; workspace + token are chosen once at setup). So a
  context switch is already a clean **cache miss** via the `Key` fingerprint — never a
  wrong-set paint — and `--reset` is the sole logout/wipe path.

## Design

Mirror the StatusCache timestamp pattern.

1. **Capture timestamp.** Add `CapturedAtMs` (epoch-ms UTC) to `TaskCacheDocument` and
   `FeedCacheDocument`; stamp it on every `Save`. Bump `CurrentSchemaVersion` 1 → 2 so any
   pre-upgrade v1 doc is discarded cleanly (one-time miss → live load).

2. **Age / TTL / eviction.** Add a `TimeProvider` seam + a `maxAge` (default **14 days**)
   to both caches (ctor-overridable for tests). On load, after the version + key checks:
   - compute `age = now - capturedAt`;
   - if `age >= maxAge` (or the timestamp is structurally invalid), it's a **miss** and the
     doc is **deleted** (age-based self-prune of a genuinely-stale snapshot). The boundary is
     exclusive (`age == maxAge` is stale), matching `StatusCache`'s `age < ttl` freshness;
   - otherwise return the payload **plus** its `CapturedAt`.

   The single-doc-per-key design already bounds the store to one task doc + one feed doc
   (Save overwrites regardless of key), so count is inherently bounded; age-eviction covers
   the "very old" dimension. No LRU needed.

3. **Snapshot return.** Add `CachedSnapshot<T>(IReadOnlyList<T> Items, DateTimeOffset CapturedAt)`
   and a `LoadSnapshot(config)` on each cache. Keep the existing `Load(config)` as a thin
   wrapper (`=> LoadSnapshot(config)?.Items`) so current callers/tests are unaffected and
   still inherit age-eviction.

4. **Staleness marker (UI).** On instant paint, surface the age:
   - tasks: `"Showing cached tasks from {age} ago · {n} task(s) · refreshing…"`;
   - feed: `"Showing cached feed from {age} ago · refreshing…"`.
   A pure `RelativeTime.Format(TimeSpan)` helper ("just now" / "3m ago" / "2h ago" /
   "5d ago") backs both and is unit-tested.

5. **Reset completeness.** Delete `StateKeys.Assignees` in the `--reset` / `--logout` block
   so logout leaves no per-workspace cache behind. Document that runtime context switches
   are already clean-miss by fingerprint.

## Phases

- **Phase 1 — service layer + tests.** `CachedSnapshot<T>`, `RelativeTime`, timestamp +
  TTL/eviction + `LoadSnapshot` on both caches, complete `--reset`. Extend
  `TaskCacheTests` / `FeedCacheTests` (timestamp round-trip, within-max-age hit,
  beyond-max-age miss+delete, v1 discard) and add `RelativeTimeTests`. Build/test/format,
  commit, push → opens draft PR.
- **Phase 2 — UI staleness marker.** Wire the age into `TodoApp` instant-paint status/flash.
  Build; verify by reasoning + `tui-validate` (described in the PR). Commit, push, mark ready.

## Acceptance mapping

- *Stale data shown for instant paint but clearly marked, replaced on refresh* → age in the
  instant-paint status/flash; live refresh swaps it (existing behaviour).
- *Cache is bounded and self-prunes; version mismatches handled without crashes* → single-doc
  bound + age-eviction; schema bump exercises the existing discard path.
- *Token/workspace change clears the relevant caches* → `--reset` now clears all cache keys
  incl. assignees; runtime switches are clean-miss by fingerprint (no wrong-set paint).
- *`dotnet test` green* → new unit tests; TUI slice via `tui-validate`.
