using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Services;

/// <summary>
/// The persisted snapshot document written under <see cref="StateKeys.Tasks"/> (#122). It carries the
/// task working set plus the <see cref="Key"/> fingerprint it was captured for and a
/// <see cref="SchemaVersion"/>, so a load can reject a payload that belongs to a different context
/// (workspace / list / assignee scope) or an older shape instead of painting the wrong set.
/// </summary>
public sealed record TaskCacheDocument
{
    /// <summary>The cache-format version; bumped if the persisted shape changes incompatibly so an old
    /// document is discarded rather than deserialised into garbage.</summary>
    public int SchemaVersion { get; init; } = TaskCache.CurrentSchemaVersion;

    /// <summary>The workspace/list/assignee fingerprint (<see cref="TaskCache.KeyFor"/>) the payload was
    /// captured under. A load only trusts the payload when this matches the current config.</summary>
    public required string Key { get; init; }

    /// <summary>The cached task working set (assigned-to-me ∪ Personal Tasks list), as last loaded.</summary>
    public required IReadOnlyList<TaskItem> Tasks { get; init; }
}

/// <summary>
/// Persists the last successfully-loaded task working set via <see cref="IStateStore"/> so the app can
/// paint instantly on launch while the live refresh runs (#122, part of Epic #118).
/// <para>
/// The working set the UI renders (<c>TodoApp._all</c>) is the merged snapshot from
/// <see cref="TaskService.LoadAsync"/> — assigned-to-me ∪ Personal Tasks list. Everything the F3 view
/// does (filter / sort / group, <c>Status IS NOT</c>, subtask nesting) is applied <b>client-side at
/// render time</b>, not in the fetch; only the workspace, the Personal Tasks list, and the
/// <c>Assignee IS</c> rules scope the server-side fetch and therefore change the set. So the cache is
/// keyed on exactly those (<see cref="KeyFor"/>): the cached superset stays valid across a pure
/// sort/group/filter change, and the caller re-applies the current view to it — an instant, still-correct
/// paint. It can never surface the wrong <em>set</em> after a context switch, because a mismatched
/// fingerprint is a clean miss.
/// </para>
/// <para>
/// TTL / staleness / eviction / full reset-on-token-or-workspace-change are out of scope here and
/// tracked by #124; this stores and restores one document, superseded on each save.
/// </para>
/// </summary>
public sealed class TaskCache(IStateStore store)
{
    /// <summary>The current <see cref="TaskCacheDocument.SchemaVersion"/>.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// The cached working set for <paramref name="config"/>'s context, or <see langword="null"/> when
    /// nothing is cached, the cached payload was captured for a different context (workspace / list /
    /// assignee scope), or it was written by an incompatible schema version. A non-null result may be
    /// empty (an empty set was genuinely cached).
    /// </summary>
    public IReadOnlyList<TaskItem>? Load(AppConfig config)
    {
        TaskCacheDocument? doc;
        try
        {
            doc = store.Load<TaskCacheDocument>(StateKeys.Tasks);
        }
        catch (JsonException)
        {
            // A corrupt cache is a miss, never a crash: the payload is rewritten (non-atomically) on
            // every changed poll, so a quit/kill/power-loss mid-write can truncate it — and this load
            // runs synchronously in Run() before the UI loop, so a throw here would brick every launch
            // until the file was hand-deleted. Falling back to the live load is exactly the safe
            // degradation the cache is meant to have. (A missing/older-shape doc missing the required
            // members surfaces as a JsonException here too, before the version check below.)
            return null;
        }
        if (doc is null || doc.SchemaVersion != CurrentSchemaVersion || doc.Key != KeyFor(config))
            return null;
        return doc.Tasks;
    }

    /// <summary>Persist <paramref name="tasks"/> as the cache for <paramref name="config"/>'s context,
    /// replacing any prior document.</summary>
    public void Save(AppConfig config, IReadOnlyList<TaskItem> tasks)
        => store.Save(StateKeys.Tasks, new TaskCacheDocument { Key = KeyFor(config), Tasks = tasks });

    /// <summary>Forget the cached working set (used by <c>--reset</c>). A no-op when nothing is cached.</summary>
    public void Clear() => store.Delete(StateKeys.Tasks);

    /// <summary>
    /// The context fingerprint that determines the fetched working set: the workspace id, the Personal
    /// Tasks list id, and the (sorted) <c>Assignee IS</c> rule values that scope the assigned fetch
    /// server-side (#68). Sort/group and non-assignee filters are deliberately excluded — they only
    /// affect client-side rendering, not the set that is fetched — so the cache survives a pure view
    /// tweak between sessions. Pure and stable (order-independent in the assignee set).
    /// </summary>
    internal static string KeyFor(AppConfig config)
    {
        // AssigneeRuleValues dedupes case-insensitively but keeps each value's original casing, so
        // normalise before joining — otherwise "Ben" and "ben" (which resolve to the same server-side
        // fetch) would fingerprint differently and cause a false miss (lost instant paint, never a
        // wrong set). Consistent with the case-insensitive matching used at the fetch layer.
        var assignees = TaskService.AssigneeRuleValues(config.View)
            .Select(v => v.ToLowerInvariant())
            .OrderBy(v => v, StringComparer.Ordinal);
        return string.Join('|', new[] { config.WorkspaceId, config.PersonalTasksListId }.Concat(assignees));
    }
}
