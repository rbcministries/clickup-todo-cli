namespace ClickUpTodo.Configuration;

/// <summary>
/// Clears every persisted cache payload (#124) on logout — the task working set (#122), the feed
/// (#123), the per-list status/color metadata (#125), the assignee-frequency pool (#155), and the warm
/// closed-task set (#280) — so <c>--reset</c> / <c>--logout</c> leaves no cache behind for a different
/// account or workspace.
/// The token and settings are forgotten separately by the caller (they are not <see cref="IStateStore"/>
/// cache keys). Centralised and key-listed here so the exact set of cleared caches is verifiable in one
/// place, rather than scattered across the composition root where a key could silently be dropped.
/// </summary>
public static class CacheReset
{
    /// <summary>Every state key holding a cache payload that a logout must forget.</summary>
    public static readonly IReadOnlyList<string> CacheKeys =
    [
        StateKeys.Tasks,
        StateKeys.Feed,
        StateKeys.Statuses,
        StateKeys.ListColors,
        StateKeys.Assignees,
        StateKeys.Closed,
    ];

    /// <summary>Delete every cache payload from <paramref name="store"/>. A no-op per key when nothing
    /// is stored under it.</summary>
    public static void ClearAll(IStateStore store)
    {
        foreach (var key in CacheKeys)
            store.Delete(key);
    }
}
