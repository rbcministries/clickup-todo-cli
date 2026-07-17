# `IFocusStore` seam (issue #22)

## Goal

Today the "Current Focus" pane is driven by a local-only `AppConfig.PinnedTaskIds`
list, toggled with `Ctrl+P` and persisted to `config.json`. `TodoApp` reaches
into pin state directly in three places (`_pinnedIds` HashSet, `TogglePin`,
`Render`). Introduce an `IFocusStore` seam so a future ClickUp **Personal
Priorities** store can slot in as a contained change, without integrating that
API now (the public API does not expose Personal Priorities yet — see #22's
links).

This is a **behaviour-preserving refactor**: pinning still persists to
`config.json` exactly as before.

## Design (from the issue)

`src/ClickUpTodo/Focus/`:

### `IFocusStore`
```csharp
public interface IFocusStore
{
    ValueTask<IReadOnlySet<string>> GetPinnedAsync(CancellationToken ct = default);
    bool IsPinned(string taskId);                 // fast, in-memory — for Render()
    ValueTask<bool> ToggleAsync(string taskId, CancellationToken ct = default); // -> new pinned state
}
```
Async-first so a network-backed implementation fits without churn.

### `LocalFocusStore(AppConfig, ConfigStore)`
Wraps today's behaviour: in-memory `HashSet<string>` seeded from
`config.PinnedTaskIds`; `ToggleAsync` flips membership, writes
`config.PinnedTaskIds`, and `store.Save(config)`. Both reads complete
synchronously (`ValueTask.FromResult`).

### `ClickUpPrioritiesFocusStore`
Documented placeholder for when ClickUp ships the Personal Priorities API.
Methods throw `NotSupportedException`; **not** wired anywhere. Kept so the
extension point is explicit in code; the real integration stays deferred (#22's
own scope note + the ClickUp feedback request track it).

## `TodoApp` wiring

- Constructor takes an `IFocusStore focus`; drop the `_pinnedIds` field.
- `TogglePin()` → fire-and-forget `TogglePinAsync(task)` that awaits
  `_focus.ToggleAsync`, then re-renders and flashes on the UI thread via
  `Application.Invoke` (keeps it async-safe for a future network store while the
  local store completes synchronously).
- `Render()` uses `_focus.IsPinned(task.Id)` instead of `_pinnedIds.Contains`.
- `Program.cs` constructs `new LocalFocusStore(config, configStore)` and passes
  it to `TodoApp`.

Single sectioned `ListView` model untouched; no keybinding/layout change; no API
or Kiota change. `config.json` `PinnedTaskIds` format unchanged (no migration).

## Tests (`LocalFocusStoreTests`, xUnit, no network)

Uses a real `ConfigStore` pointed at a temp dir (mirrors `TokenStoreTests`):

- Seeds `_ids` from existing `config.PinnedTaskIds`; `IsPinned`/`GetPinnedAsync`
  reflect them.
- `ToggleAsync` pins an unpinned task (returns `true`, `IsPinned` true) and
  unpins a pinned one (returns `false`).
- Toggling persists: `config.PinnedTaskIds` updated **and** written to disk
  (re-`Load()` from a fresh store sees the change).
- `GetPinnedAsync` returns the current set.

## Deferred

Actual ClickUp Personal Priorities integration (`ClickUpPrioritiesFocusStore`
body) — blocked on the API; tracked by #22's references. Recommend a dedicated
follow-up issue when #22 closes.
