namespace ClickUpTodo.Configuration;

/// <summary>
/// Well-known keys for state persisted through <see cref="IStateStore"/>. Each key names one logical
/// document; the file backend maps it to <c>{key}.json</c>, a collection backend to a collection.
/// Cache-layer issues (#122 tasks, #123 feed, #125 statuses/colors) add their own keys here.
/// </summary>
public static class StateKeys
{
    /// <summary>
    /// The app's settings document — <see cref="AppConfig"/>, including the focus pins
    /// (<see cref="AppConfig.PinnedTaskIds"/>). Maps to <c>config.json</c> in the file backend.
    /// </summary>
    public const string Config = "config";

    /// <summary>
    /// The persisted task working-set cache (#122) — the last successfully-loaded snapshot, so the app
    /// can paint instantly on launch while the live refresh runs. Maps to <c>tasks.json</c> in the file
    /// backend. One document; the stored payload carries the workspace/list/assignee fingerprint it was
    /// written for, so a context switch is a clean cache miss rather than a stale paint.
    /// </summary>
    public const string Tasks = "tasks";

    /// <summary>
    /// The assignee-frequency candidate pool (#155) — most-frequent assignees across the loaded task
    /// lists, plus a deferred workspace-members top-up, scoped to one workspace. Maps to
    /// <c>assignees.json</c> in the file backend.
    /// </summary>
    public const string Assignees = "assignees";

    /// <summary>
    /// The persisted mentions/comments feed cache (#123) — the last successfully-aggregated feed, so
    /// opening the feed screen paints instantly while the live refresh runs. Maps to <c>feed.json</c>
    /// in the file backend. One document; the stored payload carries the workspace/assignee fingerprint
    /// it was written for, so a context switch is a clean cache miss rather than a stale paint.
    /// </summary>
    public const string Feed = "feed";
}
