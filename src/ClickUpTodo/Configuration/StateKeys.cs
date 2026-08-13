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

    /// <summary>
    /// The per-list status-options cache (#125) — the statuses <see cref="Services.StatusCache"/> warms
    /// from on launch so the picker opens without a first-load round-trip. Each entry carries its capture
    /// timestamp, so the cache's TTL still applies to a persisted entry (nothing stale is served past
    /// expiry). Scoped to one workspace; a mismatch is a clean miss. Maps to <c>statuses.json</c>.
    /// </summary>
    public const string Statuses = "statuses";

    /// <summary>
    /// The list-frequency candidate pool (#238) — most-frequent lists across the loaded task rows, plus
    /// a count-0 long-tail backfill from the scheduled list-hierarchy walk (#236), scoped to one
    /// workspace. Maps to <c>lists.json</c> in the file backend.
    /// </summary>
    public const string Lists = "lists";

    /// <summary>
    /// The per-list color-chip cache (#125) — the resolved list colors <see cref="Services.TaskService"/>
    /// uses to tint List-grouped headers, warmed on launch to avoid re-resolving every color at first
    /// render. Each entry carries its capture timestamp, so a persisted color expires after the color TTL
    /// even though colors are held for the process lifetime in memory. Scoped to one workspace. Maps to
    /// <c>listColors.json</c>.
    /// </summary>
    public const string ListColors = "listColors";

    /// <summary>
    /// The warm closed-task set (#280, follow-up to #253) — the bounded, recently-closed tasks
    /// <see cref="Services.ClosedTaskCache"/> bridge-paints at the F12→All transition, persisted so the
    /// very first transition after a fresh launch is instant too (rather than stalling one poll interval
    /// until the background prefetch warms the in-memory set). One document; the stored payload carries
    /// the workspace/list/assignee fingerprint it was captured under (a context switch is a clean miss),
    /// and the per-task age window is re-applied on load so a stale set self-prunes. Maps to
    /// <c>closed.json</c> in the file backend.
    /// </summary>
    public const string Closed = "closed";

    /// <summary>
    /// The local Super Agent directory (#494) — the discovered-layer <c>name → negative id</c> registry
    /// <see cref="Services.AgentDirectoryCache"/> warms from on launch, standing in for ClickUp's missing
    /// agent-enumeration endpoint. Each entry carries its capture timestamp so a persisted id expires
    /// after the registry's TTL (a stale id points at nothing once the agent is recreated). Scoped to one
    /// workspace; a mismatch is a clean miss. The hand-pinned config seed is separate (it lives in
    /// <see cref="AppConfig"/>). Maps to <c>agentDirectories.json</c> in the file backend.
    /// </summary>
    public const string AgentDirectories = "agentDirectories";
}
