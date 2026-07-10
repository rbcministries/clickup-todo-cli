using System.Globalization;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Services;

/// <summary>
/// Result of <see cref="TaskService.LoadSnapshotAsync"/> (#194): the merged snapshot, whether it
/// differs from the previous one (<see cref="Changed"/> is false only for a provably-empty delta, so
/// callers can skip snapshot-dependent follow-up work), and whether it was produced by a delta fetch
/// (<see cref="WasDelta"/>, which callers use to drive their periodic full-resync cadence).
/// </summary>
public sealed record TaskSnapshotResult(IReadOnlyList<TaskItem> Tasks, bool Changed, bool WasDelta);

/// <summary>
/// Fetches and merges the user's actionable tasks (assigned-to-me ∪ Personal Tasks list),
/// de-duplicated and stably ordered, and resolves per-list status options on demand (cached).
/// </summary>
public sealed class TaskService(IClickUpClient client, AppConfig config, long userId, TimeProvider? timeProvider = null)
{
    // Per-list status options, cached with a long TTL (statuses rarely change) and warmed by
    // PrefetchStatusesAsync so the picker opens from cache in the common case.
    private readonly StatusCache _statusCache = new(client.GetListStatusesAsync, timeProvider);

    // Per-list color chips, cached for the process lifetime (a list's color effectively never changes
    // within a session). A null value means "fetched, but the list has no color set" — cached so it
    // isn't refetched. Used to tint List-grouped headers (#61).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _listColors = new(StringComparer.Ordinal);

    /// <summary>The signed-in app user's ClickUp id — the target of the default "Assignee IS me" rule.</summary>
    public long UserId { get; } = userId;

    /// <summary>
    /// Clock-skew allowance subtracted from the watermark on every delta query (#194): ClickUp's
    /// <c>date_updated</c> stamps come from its servers, ours from this machine, so the query
    /// deliberately re-reads a minute of overlap. Harmless — the merge upserts by id, so a re-fetched
    /// unchanged task is idempotent.
    /// </summary>
    internal const long DeltaSkewMs = 60_000;

    // Delta-refresh state (#194): the last snapshot this service produced and the newest
    // date_updated seen in it. Null until the first successful full load — and reset to null by it —
    // so a delta can never run against a stale baseline. Written only from LoadSnapshotAsync.
    private IReadOnlyList<TaskItem>? _lastSnapshot;
    private long? _watermarkMs;

    /// <summary>
    /// Loads the task snapshot, incrementally when possible (#194). With <paramref name="preferDelta"/>
    /// set and a previous snapshot + watermark available, only tasks updated since the watermark are
    /// fetched (closed included, so completions surface) and merged into the previous snapshot —
    /// the steady-state poll cost drops from a full re-fetch to one or two tiny requests. Otherwise —
    /// first load, caller wants guaranteed freshness (manual refresh, F3 fetch-rule change, periodic
    /// resync), or nothing fetched yet carried a usable <c>date_updated</c> — it falls back to the full
    /// <see cref="LoadAsync"/>. <see cref="TaskSnapshotResult.Changed"/> is false only when a delta
    /// provably changed nothing, so the caller can skip snapshot-dependent follow-up work.
    /// <para>
    /// A delta cannot observe a task leaving the fetch's scope without an update it would match —
    /// e.g. unassigned from me by someone else. That staleness is bounded by the caller's periodic
    /// full-resync cadence (and any manual refresh), not handled here.
    /// </para>
    /// </summary>
    public async Task<TaskSnapshotResult> LoadSnapshotAsync(bool preferDelta, CancellationToken ct = default)
    {
        if (preferDelta && _lastSnapshot is { } previous && _watermarkMs is { } watermark)
        {
            var since = Math.Max(0, watermark - DeltaSkewMs);
            // Both delta fetches are independent; overlap them (same shape as the full load, #192).
            var personalFetch = client.GetListTasksDeltaAsync(config.PersonalTasksListId, since, ct);
            var assignedFetch = LoadAssignedDeltaAsync(since, ct);
            await Task.WhenAll(assignedFetch, personalFetch);

            // Personal-list results win id collisions, matching LoadAsync's merge order.
            var delta = (await assignedFetch).Concat(await personalFetch).ToList();
            var (merged, changed) = MergeDelta(previous, delta);
            _watermarkMs = MaxUpdatedMs(delta) is { } newest ? Math.Max(watermark, newest) : watermark;
            _lastSnapshot = merged;
            return new TaskSnapshotResult(merged, changed, WasDelta: true);
        }

        var tasks = await LoadAsync(ct);
        _lastSnapshot = tasks;
        _watermarkMs = MaxUpdatedMs(tasks);
        return new TaskSnapshotResult(tasks, Changed: true, WasDelta: false);
    }

    private async Task<List<TaskItem>> LoadAssignedDeltaAsync(long since, CancellationToken ct)
        => await client.GetAssignedTasksDeltaAsync(
            config.WorkspaceId, await ResolveAssigneeIdsAsync(config.View, ct), since, ct);

    /// <summary>
    /// Merges a delta fetch into the previous snapshot (#194): a delta task whose status type is
    /// <c>closed</c> is removed (the full fetch's server-side <c>include_closed=false</c> filter,
    /// re-applied client-side), every other delta task is upserted by id, and the result is re-sorted
    /// with the standard <see cref="TaskOrder"/>. Returns <c>Changed=false</c> — previous list
    /// instance included, so no re-render churn — when the delta was empty or only "removed" tasks
    /// that were never present. Pure and unit-testable.
    /// </summary>
    internal static (IReadOnlyList<TaskItem> Tasks, bool Changed) MergeDelta(
        IReadOnlyList<TaskItem> previous, IReadOnlyList<TaskItem> delta)
    {
        if (delta.Count == 0)
            return (previous, false);

        var byId = new Dictionary<string, TaskItem>(StringComparer.Ordinal);
        foreach (var task in previous)
            byId[task.Id] = task;

        var changed = false;
        foreach (var task in delta)
        {
            if (string.IsNullOrEmpty(task.Id))
                continue;
            if (IsClosed(task))
                changed |= byId.Remove(task.Id);
            else
            {
                changed = true; // an upsert always counts: even an identical-looking task may carry
                                // field changes outside the mapped shape, and false negatives only
                                // cost a redraw while false positives would freeze the view.
                byId[task.Id] = task;
            }
        }

        return changed
            ? (byId.Values.OrderBy(t => t, TaskOrder.Instance).ToList(), true)
            : (previous, false);
    }

    private static bool IsClosed(TaskItem task)
        => string.Equals(task.StatusType, "closed", StringComparison.OrdinalIgnoreCase);

    /// <summary>The newest <see cref="TaskItem.UpdatedMs"/> in <paramref name="tasks"/>, or null when
    /// none carries one (in which case delta refresh stays disabled until a full load provides one).</summary>
    internal static long? MaxUpdatedMs(IEnumerable<TaskItem> tasks)
    {
        long? max = null;
        foreach (var t in tasks)
        {
            if (t.UpdatedMs is { } ms && (max is null || ms > max))
                max = ms;
        }
        return max;
    }

    /// <summary>Merged, de-duplicated, stably-ordered task snapshot.</summary>
    public async Task<IReadOnlyList<TaskItem>> LoadAsync(CancellationToken ct = default)
    {
        // The two source fetches are independent, so the personal-list fetch overlaps assignee-id
        // resolution + the assigned fetch (#192): wall-clock is the slower of the two, not their sum.
        // WhenAll (rather than awaiting in turn) so a fault in one still observes the other.
        var personalFetch = client.GetListTasksAsync(config.PersonalTasksListId, ct: ct);
        var assignedFetch = LoadAssignedAsync(ct);
        await Task.WhenAll(assignedFetch, personalFetch);
        var assigned = await assignedFetch;
        var personal = await personalFetch;

        // De-dup by task id; a task assigned to me that also lives on my personal list appears once.
        var byId = new Dictionary<string, TaskItem>();
        foreach (var task in assigned.Concat(personal))
            byId[task.Id] = task;

        // Status exclusion is no longer a separate mechanism — it's ordinary "Status IS NOT" filter
        // rules applied by TaskView.Filter at render time (#69). LoadAsync only fetches, merges, and
        // orders; visibility is decided in exactly one place.
        return byId.Values
            .OrderBy(t => t, TaskOrder.Instance)
            .ToList();
    }

    // Assignee IS rules scope the assigned fetch server-side (#68). The default view's "Assignee IS
    // me" resolves to [userId] — today's behaviour; an empty set (rule cleared) fetches everyone. A
    // username/email rule is resolved to an id via the workspace-members lookup (#73).
    private async Task<List<TaskItem>> LoadAssignedAsync(CancellationToken ct)
        => await client.GetAssignedTasksAsync(config.WorkspaceId, await ResolveAssigneeIdsAsync(config.View, ct), ct);

    /// <summary>
    /// Cap on concurrent round-trips per fan-out (context parents, list colors). Small and fixed:
    /// enough to hide per-call latency, low enough to stay polite to ClickUp's per-token rate limit
    /// even when several fan-outs overlap (#192) — a process-wide budget is tracked in #193.
    /// </summary>
    internal const int MaxFanOutConcurrency = 4;

    // Workspace members, fetched at most once per service instance (they change rarely within a session)
    // and only when an Assignee rule actually needs a name/email resolved. A faulted fetch is not cached
    // so the next load retries.
    private Task<IReadOnlyList<WorkspaceMember>>? _membersFetch;

    /// <summary>The set of assignee ids the assigned fetch should be scoped to (me + numeric ids only,
    /// for this service's user). Name/email values are resolved by <see cref="ResolveAssigneeIdsAsync"/>.</summary>
    public IReadOnlyList<long> ResolveAssigneeIds(ViewSettings view) => ResolveAssigneeIds(view, UserId);

    /// <summary>Me + numeric-id resolution only; a username/email value contributes nothing (it needs the
    /// members overload). Kept for the fast path and as the members-fetch-failure fallback.</summary>
    public static IReadOnlyList<long> ResolveAssigneeIds(ViewSettings view, long currentUserId)
        => ResolveAssigneeIds(view, currentUserId, []);

    /// <summary>
    /// The assignee ids to send to the server-side task fetch, derived from the view's
    /// <c>Assignee IS</c> rules: the <c>me</c> token resolves to <paramref name="currentUserId"/>, a
    /// numeric value is taken as an id, and any other value (a username/email) is matched
    /// case-insensitively against <paramref name="members"/>' username/email and resolved to their id(s)
    /// (#73). A value that matches no member is skipped (best-effort). An empty result means "no assignee
    /// filter" (fetch everyone). Pure and unit-testable.
    /// <para>
    /// Multiple <c>Assignee IS</c> rules union into one set — ClickUp's <c>assignees[]</c> is OR
    /// (assigned to <em>any</em>), which is the right contains-semantics for a multi-valued field even
    /// though the other F3 rule kinds AND together. The default view has a single rule, so this only
    /// matters once a user adds a second assignee.
    /// </para>
    /// </summary>
    public static IReadOnlyList<long> ResolveAssigneeIds(ViewSettings view, long currentUserId, IReadOnlyList<WorkspaceMember> members)
    {
        var ids = new List<long>();
        foreach (var r in view.Filters)
        {
            if (r.Field != TaskField.Assignee || r.Op != FilterOp.Is)
                continue;
            var value = r.Value?.Trim() ?? "";
            if (value.Length == 0)
                continue;
            if (string.Equals(value, ViewSettings.CurrentUserToken, StringComparison.OrdinalIgnoreCase))
                ids.Add(currentUserId);
            else if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                ids.Add(id);
            else
                ids.AddRange(MatchMembers(members, value));
        }
        return ids.Distinct().ToList();
    }

    /// <summary>Ids of members whose username or email equals <paramref name="value"/> (case-insensitive).</summary>
    private static IEnumerable<long> MatchMembers(IReadOnlyList<WorkspaceMember> members, string value)
        => members
            .Where(m => string.Equals(m.Username, value, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(m.Email, value, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Id)
            .Where(id => id != 0);

    /// <summary>True when a view has an <c>Assignee IS</c> rule whose value is neither <c>me</c> nor a
    /// numeric id — i.e. a username/email that requires the workspace-members lookup to resolve. Used to
    /// avoid the members round-trip on the common (default) view.</summary>
    public static bool HasUnresolvedAssigneeNames(ViewSettings view)
        => view.Filters.Any(r => r.Field == TaskField.Assignee && r.Op == FilterOp.Is && IsUnresolvedName(r.Value));

    private static bool IsUnresolvedName(string? value)
    {
        var v = value?.Trim() ?? "";
        return v.Length > 0
            && !string.Equals(v, ViewSettings.CurrentUserToken, StringComparison.OrdinalIgnoreCase)
            && !long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// Resolves the view's <c>Assignee IS</c> rules to a server-side assignee-id set, fetching workspace
    /// members (cached, at most once) only when a rule carries a username/email. Best-effort: if the
    /// members fetch fails, falls back to me + numeric ids so the load still succeeds (#73).
    /// </summary>
    public async Task<IReadOnlyList<long>> ResolveAssigneeIdsAsync(ViewSettings view, CancellationToken ct = default)
    {
        if (!HasUnresolvedAssigneeNames(view))
            return ResolveAssigneeIds(view);

        IReadOnlyList<WorkspaceMember> members;
        try
        {
            members = await (_membersFetch ??= client.GetWorkspaceMembersAsync(config.WorkspaceId, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine caller cancellation (e.g. app shutdown) — let it propagate
        }
        catch (Exception)
        {
            // Any failure — an API error or an HttpClient timeout (which surfaces as a
            // TaskCanceledException even though our ct wasn't signalled) — is best-effort: names stay
            // unresolved (me/numeric still apply) and the cache is cleared so the next load retries.
            _membersFetch = null;
            return ResolveAssigneeIds(view);
        }
        return ResolveAssigneeIds(view, UserId, members);
    }

    /// <summary>The distinct, case-insensitive set of <c>Assignee IS</c> rule <em>values</em>. Used to
    /// decide whether an F3 edit changed the server-side fetch (needing a reload) rather than just the
    /// client-side view. Compares raw values, not resolved ids, so a change to a still-unresolved
    /// username/email is never missed (unlike an id-set comparison, which can't see it).</summary>
    public static IReadOnlySet<string> AssigneeRuleValues(ViewSettings view)
        => view.Filters
            .Where(r => r.Field == TaskField.Assignee && r.Op == FilterOp.Is)
            .Select(r => (r.Value ?? "").Trim())
            .Where(v => v.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The available statuses for a list, served from the TTL cache or fetched on demand.</summary>
    public Task<IReadOnlyList<StatusOption>> GetStatusesForListAsync(string listId, CancellationToken ct = default)
        => _statusCache.GetAsync(listId, ct);

    /// <summary>A list's statuses if cached and still fresh, without a fetch (for opening the picker instantly).</summary>
    public bool TryGetCachedStatuses(string listId, out IReadOnlyList<StatusOption> statuses)
        => _statusCache.TryGetFresh(listId, out statuses);

    /// <summary>Warms the status cache for the given lists (best-effort) so the picker opens from cache.</summary>
    public Task PrefetchStatusesAsync(IEnumerable<string> listIds, CancellationToken ct = default)
        => _statusCache.PrefetchAsync(listIds, ct);

    /// <summary>
    /// Sets a task's status and returns the <b>confirmed</b> status name from the write response
    /// (or null if the API omitted it), so the UI can show the server-confirmed value.
    /// </summary>
    public Task<string?> SetStatusAsync(string taskId, string statusName, CancellationToken ct = default)
        => client.SetTaskStatusAsync(taskId, statusName, ct);

    /// <summary>Full detail for a single task, fetched on demand for the detail view (#17).</summary>
    public Task<TaskDetail> GetTaskDetailAsync(string taskId, CancellationToken ct = default)
        => client.GetTaskDetailAsync(taskId, ct);

    /// <summary>The comments on a task, for the detail view's Comments tab (#17).</summary>
    public Task<IReadOnlyList<CommentItem>> GetTaskCommentsAsync(string taskId, CancellationToken ct = default)
        => client.GetTaskCommentsAsync(taskId, ct);

    /// <summary>
    /// Returns a new snapshot with the task identified by <paramref name="taskId"/> carrying
    /// <paramref name="newStatus"/>, leaving every other task and the overall order untouched. Pure
    /// (the input list is not mutated) so the TUI can update one record in place without a reload.
    /// </summary>
    public static IReadOnlyList<TaskItem> ApplyStatusChange(IReadOnlyList<TaskItem> tasks, string taskId, string? newStatus)
        => tasks.Select(t => t.Id == taskId ? t with { StatusName = newStatus } : t).ToList();

    /// <summary>
    /// The distinct parent ids referenced by a subtask in <paramref name="snapshot"/> that aren't
    /// themselves present in it — the parents the nested subtasks view (#46) must pull in as context
    /// headers. Pure; order follows first appearance so the fetch <em>start</em> order is deterministic
    /// (completion order — and the resulting dictionary — is not, under the bounded fan-out).
    /// </summary>
    internal static IReadOnlyList<string> MissingParentIds(IReadOnlyList<TaskItem> snapshot)
    {
        var present = new HashSet<string>(snapshot.Select(t => t.Id));
        var missing = new List<string>();
        var seen = new HashSet<string>();
        foreach (var t in snapshot)
        {
            if (string.IsNullOrEmpty(t.ParentId) || present.Contains(t.ParentId))
                continue;
            if (seen.Add(t.ParentId))
                missing.Add(t.ParentId);
        }
        return missing;
    }

    /// <summary>
    /// Best-effort list-color lookup for the given lists, keyed by list id, for tinting List-grouped
    /// headers. Colors are cached for the process lifetime; a list whose color can't be fetched (or that
    /// has none) maps to null so the caller falls back to a generated hue and it isn't refetched.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string?>> ResolveListColorsAsync(
        IEnumerable<string> listIds, CancellationToken ct = default)
    {
        // Fetch the not-yet-cached lists concurrently; a session commonly spans several lists, so doing
        // them sequentially would add a round-trip per list to the first List-grouped render. Bounded
        // (was an unbounded WhenAll) so a many-list workspace can't burst-open a call per list (#192).
        var toFetch = listIds
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .Where(id => !_listColors.ContainsKey(id))
            .ToList();

        await Parallel.ForEachAsync(
            toFetch,
            new ParallelOptions { MaxDegreeOfParallelism = MaxFanOutConcurrency, CancellationToken = ct },
            async (id, token) =>
            {
                try
                {
                    _listColors[id] = await client.GetListColorAsync(id, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _listColors[id] = null; // best-effort: a list we can't fetch just falls back to a hue
                }
            });

        return new Dictionary<string, string?>(_listColors, StringComparer.Ordinal);
    }

    /// <summary>
    /// Fetches the parents of assigned subtasks that aren't themselves in <paramref name="snapshot"/>,
    /// mapped to <see cref="TaskItem"/> headers for the nested subtasks view. The per-parent fetches
    /// fan out with at most <see cref="MaxFanOutConcurrency"/> in flight (#192) — they were serial,
    /// which made this stage scale linearly with the number of foreign parents. Best-effort: a parent
    /// that can't be fetched (deleted / no access) is skipped rather than failing the whole load.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, TaskItem>> ResolveContextParentsAsync(
        IReadOnlyList<TaskItem> snapshot, CancellationToken ct = default)
    {
        var result = new System.Collections.Concurrent.ConcurrentDictionary<string, TaskItem>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            MissingParentIds(snapshot),
            new ParallelOptions { MaxDegreeOfParallelism = MaxFanOutConcurrency, CancellationToken = ct },
            async (id, token) =>
            {
                try
                {
                    var d = await client.GetTaskDetailAsync(id, token);
                    // ParentId is intentionally left null: a context parent is a header for its subtask, so
                    // it's always rendered at the top level (it isn't nested under its own parent here).
                    result[id] = new TaskItem
                    {
                        Id = d.Id,
                        Name = d.Name,
                        Url = d.Url,
                        StatusName = d.StatusName,
                        StatusColor = d.StatusColor,
                        ListId = d.ListId,
                        ListName = d.ListName,
                        DueDateMs = d.DueDateMs,
                        UpdatedMs = d.UpdatedMs,
                    };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Best-effort: a parent we can't fetch just won't get a context header.
                }
            });
        return result;
    }

    /// <summary>
    /// The tasks from <paramref name="fetched"/> to pull into the view as not-mine subtasks of an
    /// in-view parent (#70): those absent from <paramref name="snapshot"/> whose <c>parent</c> chain
    /// reaches a task that <em>is</em> in the snapshot. Grandchildren are included (a chain through
    /// other not-in-snapshot children still counts), and a task already in the snapshot is never
    /// duplicated. Pure and order-deterministic (follows <paramref name="fetched"/> order),
    /// cycle-guarded, deduped by id — so the fetch selection is unit-testable independent of how the
    /// pool was gathered (per-parent today, #87 may vary it).
    /// </summary>
    internal static IReadOnlyList<TaskItem> ForeignDescendants(
        IReadOnlyList<TaskItem> snapshot, IReadOnlyList<TaskItem> fetched)
    {
        var present = new HashSet<string>(snapshot.Select(t => t.Id));

        // parent-of lookup across snapshot ∪ fetched; snapshot wins on id collisions (its mapping is
        // the one the rest of the view uses).
        var parentOf = new Dictionary<string, string?>();
        foreach (var t in fetched)
            parentOf[t.Id] = t.ParentId;
        foreach (var t in snapshot)
            parentOf[t.Id] = t.ParentId;

        bool DescendsFromPresent(string id)
        {
            var seen = new HashSet<string>();
            var current = id;
            while (parentOf.TryGetValue(current, out var parent) && !string.IsNullOrEmpty(parent))
            {
                if (!seen.Add(parent))
                    return false; // pathological parent cycle — bail rather than loop forever
                if (present.Contains(parent))
                    return true;
                current = parent;
            }
            return false;
        }

        var result = new List<TaskItem>();
        var added = new HashSet<string>();
        foreach (var t in fetched)
        {
            if (present.Contains(t.Id) || !added.Add(t.Id))
                continue;
            if (DescendsFromPresent(t.Id))
                result.Add(t);
        }
        return result;
    }

    /// <summary>
    /// Fetches the teammate-owned subtasks of in-view parents so they can nest beneath them regardless
    /// of assignee (#70). The assignee constraint is server-side (#68), so these children fall outside
    /// the main fetch; we recover them via an <b>adaptive</b> plan (<see cref="SubtaskFetchStrategy"/>,
    /// #87) that picks the fetch shape from the shape of the snapshot, then runs the pooled result through
    /// the pure <see cref="ForeignDescendants"/> selector for dedup / present-exclusion / cycle-safety.
    /// <para>
    /// <b>Per-parent</b> (<see cref="ClickUpClient.GetSubtasksAsync"/> —
    /// <c>GET /task/{id}?include_subtasks=true</c>): one round-trip per parent, minimal payload, and works
    /// even when a subtask lives in a <em>different</em> list than its parent; each pulled-in child is
    /// recursed into so deeper descendants are gathered. Used for the whole snapshot when parents are few,
    /// and for the sparse remainder otherwise. <b>Whole-list</b>
    /// (<see cref="ClickUpClient.GetListTasksAsync"/> — <c>GET /list/{id}/task?subtasks=true</c>): one
    /// round-trip pulls a list entire (intra-list chains of any depth included, so no recursion needed),
    /// chosen for lists where enough in-view parents cluster that it beats the per-parent calls it
    /// replaces. Its one tradeoff is that a routed parent's <em>cross-list</em> descendants aren't
    /// recovered by that branch (the pre-#84 limitation) — only heavily-clustered lists take that path;
    /// see <c>.claude/plans/adaptive-subtask-fetch.md</c>.
    /// </para>
    /// Worst cases are bounded: the plan caps the whole-list and per-parent <em>seeds</em>, and the
    /// per-parent BFS below counts <em>every</em> <c>GetSubtasksAsync</c> round-trip (seeds + recursion)
    /// against <see cref="SubtaskFetchOptions.MaxPerParentFetches"/> so a deep/wide foreign subtree can't
    /// blow the budget. Any cap that drops work sets <see cref="ForeignSubtaskResolution.Truncated"/> so
    /// the caller can surface it (the TUI appends a note to the post-refresh status line) rather than
    /// truncating silently.
    /// Best-effort: a task/list whose fetch fails is skipped rather than failing the whole load.
    /// Non-assignee filters (status/closed) still apply — the pulled-in children flow through
    /// <c>TaskView.Apply</c> like any other task.
    /// </summary>
    public async Task<ForeignSubtaskResolution> ResolveForeignSubtasksAsync(
        IReadOnlyList<TaskItem> snapshot, SubtaskFetchOptions? options = null, CancellationToken ct = default)
    {
        var opts = options ?? SubtaskFetchOptions.Default;
        var plan = SubtaskFetchStrategy.Plan(snapshot, opts);
        var truncated = plan.Truncated;

        var fetched = new Dictionary<string, TaskItem>(StringComparer.Ordinal);

        // Whole-list branch: one round-trip per dense list; ForeignDescendants narrows the whole list to
        // the real descendants of in-view parents. Include closed tasks so a closed intermediate parent
        // doesn't break the chain to an open descendant — parity with the per-parent GetSubtasksAsync,
        // which keeps closed; TaskView.Apply does the status filtering downstream either way. Best-effort.
        foreach (var listId in plan.WholeListIds)
        {
            IReadOnlyList<TaskItem> listTasks;
            try
            {
                listTasks = await client.GetListTasksAsync(listId, includeClosed: true, ct: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue; // best-effort: a list we can't fetch contributes nothing
            }
            foreach (var t in listTasks)
            {
                if (!string.IsNullOrEmpty(t.Id))
                    fetched.TryAdd(t.Id, t);
            }
        }

        // Per-parent branch: seed the BFS only from the parents the plan left to us, recursing into each
        // pulled-in child so deeper / cross-list descendants are reached. A child already gathered by a
        // whole-list fetch is skipped (and not re-enqueued) by the dedup add below. The budget bounds the
        // TOTAL round-trips (seeds + recursion), not just the seed count, so an unexpectedly deep/wide
        // subtree stops at the cap and flags truncation instead of fanning out unboundedly.
        var budget = opts.MaxPerParentFetches;
        var spent = 0;
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var toExpand = new Queue<string>(plan.PerParentIds);
        while (toExpand.Count > 0)
        {
            var id = toExpand.Dequeue();
            if (!expanded.Add(id))
                continue;
            if (spent >= budget)
            {
                truncated = true; // still-pending ids we won't reach this refresh
                break;
            }
            spent++;
            IReadOnlyList<TaskItem> children;
            try
            {
                children = await client.GetSubtasksAsync(id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue; // best-effort: a task whose subtasks we can't fetch contributes nothing
            }
            foreach (var child in children)
            {
                if (string.IsNullOrEmpty(child.Id) || !fetched.TryAdd(child.Id, child))
                    continue;
                toExpand.Enqueue(child.Id); // its own subtasks may be foreign too
            }
        }

        return new ForeignSubtaskResolution(ForeignDescendants(snapshot, fetched.Values.ToList()), truncated);
    }

    /// <summary>Stable ordering: by due date (soonest first, undated last), then by name.</summary>
    private sealed class TaskOrder : IComparer<TaskItem>
    {
        public static readonly TaskOrder Instance = new();

        public int Compare(TaskItem? x, TaskItem? y)
        {
            if (x is null || y is null)
                return Comparer<object?>.Default.Compare(x, y);

            var dx = x.DueDateMs ?? long.MaxValue;
            var dy = y.DueDateMs ?? long.MaxValue;
            if (dx != dy)
                return dx.CompareTo(dy);

            var byName = string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.CompareOrdinal(x.Id, y.Id);
        }
    }
}
