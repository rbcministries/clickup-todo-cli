using ClickUpTodo.Configuration;

namespace ClickUpTodo.Focus;

/// <summary>
/// The default <see cref="IFocusStore"/>: pinned task ids live in <c>config.json</c>
/// (<see cref="AppConfig.PinnedTaskIds"/>) and are toggled locally. This wraps the behaviour the
/// TUI had before the seam was introduced — no functional change, no migration.
/// </summary>
public sealed class LocalFocusStore : IFocusStore
{
    private readonly AppConfig _config;
    private readonly ConfigStore _store;
    private readonly HashSet<string> _ids;

    public LocalFocusStore(AppConfig config, ConfigStore store)
    {
        _config = config;
        _store = store;
        _ids = [.. config.PinnedTaskIds];
    }

    public ValueTask<IReadOnlySet<string>> GetPinnedAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlySet<string>>(_ids);

    public bool IsPinned(string taskId) => _ids.Contains(taskId);

    public ValueTask<bool> ToggleAsync(string taskId, CancellationToken ct = default)
    {
        // Remove returns true if it was pinned (so now unpinned); otherwise add it (now pinned).
        var nowPinned = !_ids.Remove(taskId) && _ids.Add(taskId);
        _config.PinnedTaskIds = [.. _ids];
        _store.Save(_config);
        return ValueTask.FromResult(nowPinned);
    }
}
