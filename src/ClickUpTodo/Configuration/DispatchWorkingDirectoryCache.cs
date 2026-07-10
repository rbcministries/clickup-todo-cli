namespace ClickUpTodo.Configuration;

/// <summary>
/// Pure read/write rules for the per-task Dispatch working-directory cache (#96). The cache lives in
/// <see cref="AppConfig.TaskWorkingDirectories"/> (task id ⇒ absolute path) and is persisted via
/// <see cref="ConfigStore"/>. Kept free of I/O and Terminal.Gui so the store-on-explicit-selection /
/// pre-fill-on-reopen / default-not-persisted logic is unit-testable independent of the UI glue.
/// </summary>
public static class DispatchWorkingDirectoryCache
{
    /// <summary>
    /// The value to pre-fill the Dispatch pane's working-dir field with when it opens for
    /// <paramref name="taskId"/>: the cached path when one is stored (and non-blank), otherwise
    /// <c>""</c> so the field starts blank and dispatch falls through to the configured default /
    /// task-derived behaviour (#98).
    /// </summary>
    public static string PreFill(IReadOnlyDictionary<string, string> cache, string taskId)
        => cache.TryGetValue(taskId, out var dir) && !string.IsNullOrWhiteSpace(dir) ? dir : "";

    /// <summary>
    /// Reconciles the cache after a dispatch for <paramref name="taskId"/>. Stores an explicit,
    /// non-default working-directory pick so the next dispatch pre-fills it; clears any stored entry
    /// when the user reverted to the default — a blank <paramref name="chosenDirectory"/>, or one
    /// equal to <paramref name="resolvedDefault"/> (the dir the configured mode would use with no
    /// pick). Mutates <paramref name="cache"/> in place and returns <c>true</c> only when it actually
    /// changed, so the caller persists (and re-writes <c>config.json</c>) exactly when needed. Paths
    /// are trimmed and compared ordinally (Linux is case-sensitive).
    /// </summary>
    public static bool Update(
        IDictionary<string, string> cache, string taskId, string? chosenDirectory, string? resolvedDefault)
    {
        var chosen = chosenDirectory?.Trim() ?? "";
        var revertedToDefault = chosen.Length == 0
            || (resolvedDefault is not null
                && string.Equals(chosen, resolvedDefault.Trim(), StringComparison.Ordinal));

        if (revertedToDefault)
            return cache.Remove(taskId);

        if (cache.TryGetValue(taskId, out var existing) && string.Equals(existing, chosen, StringComparison.Ordinal))
            return false;

        cache[taskId] = chosen;
        return true;
    }
}
