namespace ClickUpTodo.Focus;

/// <summary>
/// Source of truth for which tasks are pinned to the "Current Focus" pane.
/// Implementations may be local (<c>config.json</c>, see <see cref="LocalFocusStore"/>) or remote
/// (ClickUp Personal Priorities, once the public API exposes it — see
/// <see cref="ClickUpPrioritiesFocusStore"/>). The interface is async-first so a network-backed
/// store can slot in without churning the call sites.
/// </summary>
public interface IFocusStore
{
    /// <summary>The currently pinned task ids. May hit the network; implementations should cache.</summary>
    ValueTask<IReadOnlySet<string>> GetPinnedAsync(CancellationToken ct = default);

    /// <summary>A fast, in-memory check used while rendering each row.</summary>
    bool IsPinned(string taskId);

    /// <summary>Pin or unpin a task. Returns the resulting pinned state (true = now pinned).</summary>
    ValueTask<bool> ToggleAsync(string taskId, CancellationToken ct = default);
}
