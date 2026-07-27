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
/// Result of one <see cref="TaskService.ResolveWorkspaceListsAsync"/> step (#236): every list
/// discovered so far this session (deduped by id) and whether the current pass has now covered
/// every space — the caller's signal to stamp its cadence gate (#246 ADR) so the next pass waits
/// a full minimum age instead of restarting immediately.
/// </summary>
public sealed record WorkspaceListsResolution(IReadOnlyList<NamedEntity> Lists, bool PassComplete);

/// <summary>
/// Fetches and merges the user's actionable tasks (assigned-to-me ∪ Personal Tasks list),
/// de-duplicated and stably ordered, and resolves per-list status options on demand (cached).
/// </summary>
public sealed class TaskService(
    IClickUpClient client, AppConfig config, long userId, TimeProvider? timeProvider = null, IStateStore? stateStore = null, string userName = "")
{
    // Per-list status options, cached with a long TTL (statuses rarely change) and warmed by
    // PrefetchStatusesAsync so the picker opens from cache in the common case. With a state store it
    // also persists across restarts (#125), warming from the last session so the picker opens without a
    // first-load round-trip; the TTL still governs a persisted entry.
    private readonly StatusCache _statusCache = new(
        client.GetListStatusesAsync, timeProvider, store: stateStore, workspaceId: config.WorkspaceId);

    // Per-list color chips (#61), used to tint List-grouped headers. Held for the process lifetime (a
    // list's color effectively never changes within a session); a null value means "fetched, no color
    // set" and is cached so it isn't refetched. With a state store it persists across restarts (#125),
    // warming resolved colors so the first List-grouped render after a restart doesn't re-resolve them.
    private readonly ListColorCache _colorCache = new(stateStore, config.WorkspaceId, timeProvider);

    /// <summary>The signed-in app user's ClickUp id — the target of the default "Assignee IS me" rule.</summary>
    public long UserId { get; } = userId;

    /// <summary>The signed-in app user's display name (empty if unknown). Used to seed the current user
    /// as the locked default assignee on the New Task screen (#213), where a blank name would be
    /// silently dropped by the assignee selector.</summary>
    public string UserName { get; } = userName;

    /// <summary>
    /// Overlap allowance subtracted from the watermark on every delta query (#194). The watermark is
    /// itself a ClickUp-server timestamp, so no local clock is involved; the minute of deliberate
    /// re-reading guards against ClickUp-side write-visibility lag and out-of-order
    /// <c>date_updated</c> stamps across its replicas (and the completion gap between the two
    /// concurrent delta fetches). Harmless — the merge upserts by id and treats a same-timestamp
    /// re-read as a no-op, so the overlap is idempotent.
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
    /// unassigned from me by someone else, moved out of scope, or <b>archived</b> (archived rows are
    /// dropped from every fetch, delta included, so unlike a closed task an archived one just stops
    /// appearing and lingers in the snapshot). That staleness is bounded by the caller's periodic
    /// full-resync cadence (and any manual refresh), not handled here.
    /// </para>
    /// </summary>
    public async Task<TaskSnapshotResult> LoadSnapshotAsync(bool preferDelta, CancellationToken ct = default)
    {
        if (preferDelta && _lastSnapshot is { } previous && _watermarkMs is { } watermark)
        {
            var since = Math.Max(0, watermark - DeltaSkewMs);
            // Both delta fetches are independent; overlap them (#192 gives the full load's pair the
            // same treatment).
            var personalFetch = client.GetListTasksDeltaAsync(config.PersonalTasksListId, since, ct);
            var assignedFetch = LoadAssignedDeltaAsync(since, ct);
            await Task.WhenAll(assignedFetch, personalFetch);

            // Union the two deltas by id BEFORE merging — personal wins collisions, matching
            // LoadAsync's merge order. Deduping here (rather than relying on MergeDelta's last-wins
            // iteration) matters because MergeDelta treats a same-timestamp upsert as a no-op re-read:
            // fed both copies sequentially it would keep the assigned one and skip the personal one.
            var delta = new Dictionary<string, TaskItem>(StringComparer.Ordinal);
            foreach (var task in (await assignedFetch).Concat(await personalFetch))
            {
                if (!string.IsNullOrEmpty(task.Id))
                    delta[task.Id] = task;
            }
            var (merged, changed) = MergeDelta(previous, delta.Values.ToList(), keepClosed: config.View.IncludesClosedTasks);
            _watermarkMs = MaxUpdatedMs(delta.Values) is { } newest ? Math.Max(watermark, newest) : watermark;
            _lastSnapshot = merged;
            return new TaskSnapshotResult(merged, changed, WasDelta: true);
        }

        var tasks = await LoadAsync(ct); // LoadAsync itself re-baselines the delta state
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
    /// <para>
    /// When <paramref name="keepClosed"/> is true (the F12 "Show Completed" toggle on, #178), a closed
    /// delta task is upserted like any other rather than removed — matching the full load, which fetches
    /// with <c>include_closed=true</c> in that mode — so a task that closes since the last snapshot stays
    /// visible instead of vanishing on the next delta.
    /// </para>
    /// </summary>
    internal static (IReadOnlyList<TaskItem> Tasks, bool Changed) MergeDelta(
        IReadOnlyList<TaskItem> previous, IReadOnlyList<TaskItem> delta, bool keepClosed = false)
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
            if (!keepClosed && IsClosed(task))
            {
                changed |= byId.Remove(task.Id);
            }
            else if (byId.TryGetValue(task.Id, out var existing)
                     && existing.UpdatedMs is { } knownMs && task.UpdatedMs == knownMs)
            {
                // The skew overlap re-reads the watermark-defining task on every poll, so a delta is
                // rarely literally empty. An upsert whose date_updated hasn't moved is provably the
                // same edit we already hold (every real ClickUp change bumps date_updated) — it must
                // not count as a change, or the steady-state Changed=false fast path would never fire.
            }
            else
            {
                changed = true; // a moved (or unknown) date_updated always counts: a spurious redraw
                                // is cheap, while a missed change would freeze the view.
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
        var snapshot = await FetchMergedAsync(config.View.IncludesClosedTasks, ct);

        // Every full load re-baselines the delta state (#194) — here, not in LoadSnapshotAsync, so a
        // direct caller can never leave the incremental path merging into a baseline older than what
        // that caller saw. The watermark only advances: this fetch excludes closed tasks, so its own
        // newest date_updated can sit behind a delta-advanced watermark (recently-closed churn), and
        // regressing to it would make every resync re-download that churn window.
        _lastSnapshot = snapshot;
        if (MaxUpdatedMs(snapshot) is { } newest && (_watermarkMs is not { } current || newest > current))
            _watermarkMs = newest;
        return snapshot;
    }

    /// <summary>
    /// Fetches and merges the two source endpoints (assigned-to-me ∪ Personal Tasks) into one
    /// de-duplicated, stably-ordered snapshot at the requested closed-task breadth. Shared by
    /// <see cref="LoadAsync"/> (which then re-baselines the delta state) and
    /// <see cref="PrefetchClosedTasksAsync"/> (which must not touch that state) — so this helper is
    /// deliberately delta-state-free.
    /// <para>
    /// The two source fetches are independent, so the personal-list fetch overlaps assignee-id
    /// resolution + the assigned fetch (#192): wall-clock is the slower of the two, not their sum.
    /// WhenAll (rather than awaiting in turn) so a fault in one still observes the other. The F12
    /// completed view (#178/#191) widens both fetches to include closed-type tasks only in its All
    /// state; when narrower, closed-type tasks are dropped server-side and TaskView hides any that still
    /// arrive (e.g. subtask anchors) — plus done-type when in Active — so hiding is consistent at every
    /// level. done-type tasks arrive regardless of this flag, so WithDone needs no wider fetch than Active.
    /// </para>
    /// </summary>
    private async Task<List<TaskItem>> FetchMergedAsync(bool includeClosed, CancellationToken ct)
    {
        var personalFetch = client.GetListTasksAsync(config.PersonalTasksListId, includeClosed, ct);
        var assignedFetch = LoadAssignedAsync(includeClosed, ct);
        await Task.WhenAll(assignedFetch, personalFetch);
        var assigned = await assignedFetch;
        var personal = await personalFetch;

        // De-dup by task id; a task assigned to me that also lives on my personal list appears once.
        var byId = new Dictionary<string, TaskItem>();
        foreach (var task in assigned.Concat(personal))
            byId[task.Id] = task;

        // Status exclusion is no longer a separate mechanism — it's ordinary "Status IS NOT" filter
        // rules applied by TaskView.Filter at render time (#69). This only fetches, merges, and orders;
        // visibility is decided in exactly one place.
        return byId.Values
            .OrderBy(t => t, TaskOrder.Instance)
            .ToList();
    }

    // Warm, bounded set of recently-closed tasks kept off the refresh loop (#253) so cycling F12 to
    // All paints instantly instead of stalling on an on-demand include_closed=true fetch. Read on the
    // UI thread (SupplementWithClosed / the bridge paint), written from the background prefetch. With a
    // state store it also persists across restarts (#280), warming from the last session's set (keyed on
    // the same workspace/list/assignee fetch scope as TaskCache) so even the first post-launch F12→All
    // is instant; the per-task age window is re-applied on load so a stale set self-prunes.
    private readonly ClosedTaskCache _closedCache = new(
        timeProvider, store: stateStore, contextKey: () => TaskCache.KeyFor(config));

    /// <summary>The warm closed-task set (newest first), empty until the first prefetch completes (#253).</summary>
    public IReadOnlyList<TaskItem> WarmClosedTasks => _closedCache.Snapshot;

    /// <summary>
    /// Refreshes the warm closed-task cache (#253): fetches the snapshot with <c>include_closed=true</c>,
    /// keeps only the closed-type tasks, and stores the bounded set. Runs on the slow cadence while the
    /// view is below All (in All the live snapshot already carries closed tasks, so the caller skips it).
    /// Deliberately does <b>not</b> touch the delta baseline (<see cref="_lastSnapshot"/> /
    /// <see cref="_watermarkMs"/>) — a wider closed fetch must never advance the open-task watermark, or
    /// the next resync would re-download the recently-closed churn window. Returns the count the bounds
    /// dropped so the caller can surface a truncation note rather than capping silently.
    /// </summary>
    public async Task<int> PrefetchClosedTasksAsync(CancellationToken ct = default)
    {
        var merged = await FetchMergedAsync(includeClosed: true, ct);
        var closed = merged.Where(IsClosed).ToList();
        return _closedCache.Update(closed);
    }

    /// <summary>
    /// Merges the warm closed set into <paramref name="snapshot"/> for the F12→All bridge paint (#253):
    /// every snapshot row is kept (the live copy wins any id collision — it's fresher), and only the
    /// closed tasks not already present are appended, then the union is re-ordered with the standard
    /// <see cref="TaskOrder"/>. Returns <paramref name="snapshot"/> unchanged when the cache is empty or
    /// adds nothing. The authoritative on-demand refresh that follows replaces the snapshot with a
    /// superset, so this is a transient bridge, never a persistent overlay. Pure and unit-testable.
    /// </summary>
    public IReadOnlyList<TaskItem> SupplementWithClosed(IReadOnlyList<TaskItem> snapshot)
    {
        var closed = _closedCache.Snapshot;
        if (closed.Count == 0)
            return snapshot;

        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in snapshot)
        {
            if (!string.IsNullOrEmpty(task.Id))
                present.Add(task.Id);
        }

        // Append only genuinely-new closed tasks; skip any already in the snapshot (the live copy wins)
        // and any blank id. Building on top of the full snapshot — rather than a keyed union — keeps
        // every snapshot row regardless of id shape, so the no-op check can't ever drop a row.
        var additions = closed
            .Where(t => !string.IsNullOrEmpty(t.Id) && !present.Contains(t.Id))
            .ToList();
        if (additions.Count == 0)
            return snapshot; // every closed task was already present — no bridge needed

        return snapshot.Concat(additions).OrderBy(t => t, TaskOrder.Instance).ToList();
    }

    // Assignee IS rules scope the assigned fetch server-side (#68). The default view's "Assignee IS
    // me" resolves to [userId] — today's behaviour; an empty set (rule cleared) fetches everyone. A
    // username/email rule is resolved to an id via the workspace-members lookup (#73).
    private async Task<List<TaskItem>> LoadAssignedAsync(bool includeClosed, CancellationToken ct)
        => await client.GetAssignedTasksAsync(config.WorkspaceId, await ResolveAssigneeIdsAsync(config.View, ct), includeClosed, ct: ct);

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

    /// <summary>
    /// Sets (or clears, when <paramref name="priorityLevel"/> is null) a task's priority and returns the
    /// <b>confirmed</b> effective level from the write response, so the UI can show the server-confirmed
    /// value. Mirrors <see cref="SetStatusAsync"/>.
    /// </summary>
    public Task<int?> SetPriorityAsync(string taskId, int? priorityLevel, CancellationToken ct = default)
        => client.SetTaskPriorityAsync(taskId, priorityLevel, ct);

    /// <summary>
    /// Adds an assignee to a task and returns the <b>server-confirmed</b> assignee set, so the UI can
    /// reconcile to the truth after an optimistic update (Quick Updates Assignees pane, #158). Thin
    /// wrapper over the facade, mirroring <see cref="SetStatusAsync"/>/<see cref="SetPriorityAsync"/>.
    /// </summary>
    public Task<IReadOnlyList<TaskAssignee>> AddAssigneeAsync(string taskId, long userId, CancellationToken ct = default)
        => client.AddTaskAssigneeAsync(taskId, userId, ct);

    /// <summary>Removes an assignee from a task and returns the server-confirmed assignee set (#158);
    /// the remove sibling of <see cref="AddAssigneeAsync"/>.</summary>
    public Task<IReadOnlyList<TaskAssignee>> RemoveAssigneeAsync(string taskId, long userId, CancellationToken ct = default)
        => client.RemoveTaskAssigneeAsync(taskId, userId, ct);

    /// <summary>
    /// Adds the task to an additional list — a "Tasks in Multiple Lists" membership (#237), consumed by
    /// the Quick Updates List pane (#242). A thin passthrough to the facade; the membership endpoint
    /// echoes no body, so callers read the confirmed set back via <see cref="GetTaskDetailAsync"/>. A
    /// disabled ClickApp surfaces as a <c>ClickUpApiException</c> for the caller to flash (non-fatal).
    /// </summary>
    public Task AddTaskToListAsync(string taskId, string listId, CancellationToken ct = default)
        => client.AddTaskToListAsync(taskId, listId, ct);

    /// <summary>Removes an additional list membership from a task (#237); the remove sibling of
    /// <see cref="AddTaskToListAsync"/>. The task's home list is unaffected.</summary>
    public Task RemoveTaskFromListAsync(string taskId, string listId, CancellationToken ct = default)
        => client.RemoveTaskFromListAsync(taskId, listId, ct);

    /// <summary>
    /// Creates a task in <paramref name="listId"/> from the given fields and returns it mapped to the
    /// domain <see cref="TaskItem"/> (#209/#213). A thin passthrough to the facade, the create sibling of
    /// <see cref="SetStatusAsync"/>/<see cref="SetPriorityAsync"/>.
    /// </summary>
    public Task<TaskItem> CreateTaskAsync(string listId, NewTaskRequest task, CancellationToken ct = default)
        => client.CreateTaskAsync(listId, task, ct);

    /// <summary>Full detail for a single task, fetched on demand for the detail view (#17).</summary>
    public Task<TaskDetail> GetTaskDetailAsync(string taskId, CancellationToken ct = default)
        => client.GetTaskDetailAsync(taskId, ct);

    /// <summary>Full detail for a task addressed by its workspace custom id (#303, Ctrl+O quick-open);
    /// the mapped <see cref="TaskDetail.Id"/> is the task's plain id.</summary>
    public Task<TaskDetail> GetTaskDetailByCustomIdAsync(string customId, string teamId, CancellationToken ct = default)
        => client.GetTaskDetailByCustomIdAsync(customId, teamId, ct);

    /// <summary>
    /// Full detail for a quick-open token that parsed as a plain id but may actually be a
    /// <b>hyphenless custom id</b> (#353): try the plain <c>GET /task/{id}</c> first, and only if that
    /// 404s — and a workspace/team id is available — retry it as a custom id via
    /// <see cref="GetTaskDetailByCustomIdAsync"/>. A non-404 failure (or a 404 with no team id to retry
    /// against) propagates unchanged; when the retry runs and the token is neither a real id nor a custom
    /// id, the retry's own not-found error surfaces. A valid plain id costs a single call — the fallback
    /// fires only on the 404.
    /// </summary>
    public async Task<TaskDetail> GetTaskDetailWithCustomIdFallbackAsync(
        string idOrCustomId, string? teamId, CancellationToken ct = default)
    {
        try
        {
            return await client.GetTaskDetailAsync(idOrCustomId, ct).ConfigureAwait(false);
        }
        catch (ClickUpApiException ex) when (ex.StatusCode == 404 && !string.IsNullOrWhiteSpace(teamId))
        {
            return await client.GetTaskDetailByCustomIdAsync(idOrCustomId, teamId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The comments on a task, for the detail view's Comments tab (#17).</summary>
    public Task<IReadOnlyList<CommentItem>> GetTaskCommentsAsync(string taskId, CancellationToken ct = default)
        => client.GetTaskCommentsAsync(taskId, ct);

    /// <summary>The most ancestry levels the Task Tree tab (#291) walks up before stopping — a shallow
    /// detail tree never needs many, and the cap bounds a pathological (or cyclic) parent chain.</summary>
    internal const int MaxAncestorFetches = 10;

    /// <summary>The most <see cref="GetSubtasksAsync"/> round-trips the Task Tree tab (#291) spends
    /// gathering descendants, bounding a deep/wide subtree so one detail view can't fan out unboundedly
    /// (mirrors the foreign-subtask budget's discipline, #87).</summary>
    internal const int MaxTreeSubtaskFetches = 25;

    /// <summary>How many <see cref="GetSubtasksAsync"/> round-trips the Task Tree descendant BFS runs
    /// concurrently within one batch (#417) — bounds the fan-out so a wide tree doesn't open a fetch per
    /// node all at once, while collapsing the previously-serial per-node latency (~30s for 13 subtasks).
    /// Mirrors <see cref="CommentThreadLoader.DefaultMaxConcurrency"/>.</summary>
    internal const int MaxTreeSubtaskConcurrency = 8;

    /// <summary>
    /// Assembles the Task Tree tab's rows (#291) for <paramref name="taskId"/>: the task's ancestry
    /// (parent chain, walked up one fetch at a time via <see cref="IClickUpClient.GetTaskItemAsync"/>),
    /// the task itself, and its descendants (a bounded BFS over <see cref="GetSubtasksAsync"/>), arranged
    /// and indented by the pure <see cref="TaskTreeArranger"/>. The ancestry walk and the descendant BFS
    /// are both <b>best-effort</b> — a failed level is skipped rather than failing the whole tree — and
    /// both are capped (<see cref="MaxAncestorFetches"/> / <see cref="MaxTreeSubtaskFetches"/>) and
    /// cycle-safe. Only the initial fetch of the task itself propagates its error, so a genuinely
    /// unreachable task surfaces as a load failure rather than an empty tree.
    /// <para>
    /// The descendant BFS fetches each frontier batch concurrently (bounded by
    /// <see cref="MaxTreeSubtaskConcurrency"/>, #417) to collapse the previously-serial per-node latency,
    /// while reproducing the serial walk's results exactly: the batch is dequeued from the front of the
    /// FIFO queue and its results are folded back <b>in that FIFO order</b>, so the fetched-parent set
    /// under the budget, the breadth-first descendant order, and the first-occurrence de-dup are all
    /// unchanged. The ancestry walk stays serial — each parent id is only known once the level below it
    /// resolves, so it is an inherent linked-list traversal with nothing to parallelize.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<TaskTreeRow>> GetTaskTreeAsync(string taskId, CancellationToken ct = default)
    {
        var current = await client.GetTaskItemAsync(taskId, ct);

        // Ancestry: one fetch per level up the parent chain, cycle-safe (the seen-set) and capped. A
        // failed parent fetch just ends the walk — the tree still shows the task + what we resolved.
        var ancestors = new List<TaskItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { current.Id };
        var parentId = current.ParentId;
        // Cap check before seen.Add so the seen-set holds only ids we actually resolved (the id we stop
        // at isn't recorded), keeping the descendant de-dup below reasoning over resolved nodes only.
        while (!string.IsNullOrEmpty(parentId) && ancestors.Count < MaxAncestorFetches && seen.Add(parentId!))
        {
            TaskItem parent;
            try
            {
                parent = await client.GetTaskItemAsync(parentId!, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                break;
            }
            ancestors.Add(parent);
            parentId = parent.ParentId;
        }
        ancestors.Reverse(); // top-most ancestor first, so it anchors the arranged chain

        // Descendants: bounded BFS over GetSubtasksAsync, deduped against the ancestry + each other and
        // best-effort per branch. The seen-set carries the ancestry ids so a subtask that echoes an
        // ancestor can't loop the tree back on itself. Each frontier batch is fetched concurrently
        // (bounded by MaxTreeSubtaskConcurrency, #417) to collapse the per-node latency, but the batch is
        // dequeued from the front of the FIFO queue and its results are folded back in that same FIFO
        // order — so the fetched-parent set under the budget, the breadth-first descendant order, and the
        // first-occurrence de-dup are identical to a strictly serial walk.
        var descendants = new List<TaskItem>();
        var descSeen = new HashSet<string>(seen, StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(taskId);
        var fetches = 0;
        while (queue.Count > 0 && fetches < MaxTreeSubtaskFetches)
        {
            // Take the next batch from the front, never exceeding the remaining fetch budget. Counting the
            // whole batch up front (before the fetch) mirrors the serial code's fetches++-before-try: a
            // branch that fails still spends its budget slot.
            var batchSize = Math.Min(Math.Min(MaxTreeSubtaskConcurrency, MaxTreeSubtaskFetches - fetches), queue.Count);
            var batch = new string[batchSize];
            for (var i = 0; i < batchSize; i++)
                batch[i] = queue.Dequeue();
            fetches += batchSize;

            var childLists = await Task.WhenAll(batch.Select(parent => FetchSubtreeChildrenAsync(parent, ct)));

            // Fold results back in the batch's FIFO order so de-dup (first-occurrence-wins) and sibling
            // order match a strictly serial BFS regardless of which fetch completed first.
            foreach (var children in childLists)
            {
                if (children is null)
                    continue; // best-effort: this branch's fetch failed
                foreach (var child in children)
                {
                    if (string.IsNullOrEmpty(child.Id) || !descSeen.Add(child.Id))
                        continue;
                    descendants.Add(child);
                    queue.Enqueue(child.Id);
                }
            }
        }

        return TaskTreeArranger.Build(taskId, ancestors, current, descendants);
    }

    /// <summary>Fetches one node's subtasks for the descendant BFS (#417), returning <c>null</c> when the
    /// fetch fails so the caller can skip that branch (best-effort per branch). A genuine
    /// <see cref="OperationCanceledException"/> propagates — matching the serial walk's
    /// <c>when (ex is not OperationCanceledException)</c> guard — so a caller cancellation still aborts.</summary>
    private async Task<IReadOnlyList<TaskItem>?> FetchSubtreeChildrenAsync(string parentId, CancellationToken ct)
    {
        try
        {
            return await client.GetSubtasksAsync(parentId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Default cap on how many reply threads
    /// <see cref="GetTaskCommentsWithRepliesAsync"/> fetches concurrently for one task's comments.</summary>
    public const int DefaultMaxReplyConcurrency = CommentThreadLoader.DefaultMaxConcurrency;

    /// <summary>
    /// The comments on a task with their reply threads loaded (#328): fetches the flat comments, then
    /// fetches replies for the comments that report a thread (<see cref="CommentItem.ReplyCount"/> &gt; 0)
    /// and attaches them to each parent's <see cref="CommentItem.Replies"/>. The returned list is the same
    /// flat top-level comments, now thread-enriched — comments without replies are unchanged and incur no
    /// extra call. Batched (bounded by <see cref="DefaultMaxReplyConcurrency"/>) and best-effort per thread
    /// so it rides the detail view's existing refresh cadence without an N+1 fetch storm.
    /// </summary>
    public async Task<IReadOnlyList<CommentItem>> GetTaskCommentsWithRepliesAsync(string taskId, CancellationToken ct = default)
    {
        var comments = await client.GetTaskCommentsAsync(taskId, ct);
        return await CommentThreadLoader.LoadRepliesAsync(
            comments, client.GetThreadedCommentsAsync, DefaultMaxReplyConcurrency, ct);
    }

    /// <summary>Posts a plain-text comment to a task (#216, over the #210 facade) and returns it as a
    /// <see cref="CommentItem"/> so the detail view can append it optimistically.</summary>
    public Task<CommentItem> CreateTaskCommentAsync(string taskId, string text, CancellationToken ct = default)
        => client.CreateTaskCommentAsync(taskId, text, ct);

    /// <summary>Writes a task's plain-text description (#217, over the #211 facade) and returns the
    /// server-confirmed value so the detail view can reflect it without a manual refresh. Pass <c>""</c>
    /// to clear the description.</summary>
    public Task<string?> SetTaskDescriptionAsync(string taskId, string description, CancellationToken ct = default)
        => client.SetTaskDescriptionAsync(taskId, description, ct);

    /// <summary>
    /// Returns a new snapshot with the task identified by <paramref name="taskId"/> carrying
    /// <paramref name="newStatus"/>, leaving every other task and the overall order untouched. Pure
    /// (the input list is not mutated) so the TUI can update one record in place without a reload.
    /// </summary>
    public static IReadOnlyList<TaskItem> ApplyStatusChange(IReadOnlyList<TaskItem> tasks, string taskId, string? newStatus)
        => tasks.Select(t => t.Id == taskId ? t with { StatusName = newStatus } : t).ToList();

    /// <summary>
    /// Returns a new snapshot with the task identified by <paramref name="taskId"/> carrying the given
    /// priority fields (level + name + colour), leaving every other task and the overall order
    /// untouched. Pure (the input list is not mutated), the priority sibling of
    /// <see cref="ApplyStatusChange"/> so the TUI can reflect an optimistic priority change in place.
    /// </summary>
    public static IReadOnlyList<TaskItem> ApplyPriorityChange(
        IReadOnlyList<TaskItem> tasks, string taskId, int? level, string? name, string? color)
        => tasks.Select(t => t.Id == taskId
            ? t with { PriorityLevel = level, PriorityName = name, PriorityColor = color }
            : t).ToList();

    /// <summary>
    /// Resolves the current record for <paramref name="taskId"/>, preferring the canonical snapshot
    /// <paramref name="primary"/> and falling back to the visible <paramref name="rows"/> — which
    /// include the context rows that live outside the snapshot (foreign subtasks #70/#179 and context
    /// parents #46). Null (header) row entries are skipped; returns null when the id is in neither.
    /// Pure and side-effect free so the Quick Updates edit-target resolution (#160) — applying an edit
    /// to a task that isn't the user's own work — is unit-testable without a TUI. Preferring
    /// <paramref name="primary"/> is safe because the caller keeps the snapshot and the visible rows in
    /// sync for any edited task, so whichever side holds it carries the current (optimistic) value.
    /// </summary>
    public static TaskItem? FindById(
        IReadOnlyList<TaskItem> primary, IEnumerable<TaskItem?> rows, string taskId)
    {
        foreach (var t in primary)
            if (t.Id == taskId)
                return t;
        foreach (var r in rows)
            if (r is not null && r.Id == taskId)
                return r;
        return null;
    }

    /// <summary>
    /// Returns a new snapshot with the task identified by <paramref name="taskId"/> carrying
    /// <paramref name="assignees"/>, leaving every other task and the overall order untouched. Pure
    /// (the input list is not mutated), the assignee sibling of <see cref="ApplyStatusChange"/> /
    /// <see cref="ApplyPriorityChange"/> so the TUI can reflect a server-confirmed assignee change in
    /// place (Quick Updates Assignees pane, #158).
    /// </summary>
    public static IReadOnlyList<TaskItem> ApplyAssigneesChange(
        IReadOnlyList<TaskItem> tasks, string taskId, IReadOnlyList<TaskAssignee> assignees)
        => tasks.Select(t => t.Id == taskId ? t with { Assignees = assignees } : t).ToList();

    /// <summary>
    /// Folds the status, priority and assignee fields of <paramref name="updated"/> onto the matching
    /// task in <paramref name="tasks"/> in one pass — the Quick Updates reconcile shared by the main-list
    /// snapshot and the single-task update target (#297), so a commit settles a field identically in
    /// both modes. Pure (the input list is not mutated); other tasks and the overall order are untouched.
    /// The <paramref name="updated"/> record always carries the current value for the fields the caller
    /// didn't touch, so applying all three never clobbers — a status/priority commit re-applies the
    /// task's existing assignees (a no-op) and an assignee change re-applies its status/priority (#158).
    /// </summary>
    public static IReadOnlyList<TaskItem> ApplyFieldChanges(IReadOnlyList<TaskItem> tasks, TaskItem updated)
    {
        tasks = ApplyStatusChange(tasks, updated.Id, updated.StatusName);
        tasks = ApplyPriorityChange(
            tasks, updated.Id, updated.PriorityLevel, updated.PriorityName, updated.PriorityColor);
        return ApplyAssigneesChange(tasks, updated.Id, updated.Assignees);
    }

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
            .Where(id => !_colorCache.Contains(id))
            .ToList();

        var resolved = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            toFetch,
            new ParallelOptions { MaxDegreeOfParallelism = MaxFanOutConcurrency, CancellationToken = ct },
            async (id, token) =>
            {
                try
                {
                    resolved[id] = await client.GetListColorAsync(id, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    resolved[id] = null; // best-effort: a list we can't fetch just falls back to a hue
                }
            });

        // Merge the freshly-resolved colors into the cache (and persist them, when a store is present),
        // then return the full known set so the caller can tint every in-view list, not just the new ones.
        _colorCache.Save(resolved);
        return _colorCache.Snapshot();
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
    /// How many spaces one <see cref="ResolveWorkspaceListsAsync"/> step walks (#236). ClickUp has no
    /// bulk all-lists endpoint, so enumeration is a Spaces → Folders/folderless → Lists walk; walking
    /// a large workspace in one shot is exactly what trips its rate limit (the SetupWizard caution).
    /// Bounding the step and resuming next refresh cycle spreads the pass instead.
    /// </summary>
    internal const int MaxSpacesPerWalkStep = 3;

    // The in-progress walk pass: spaces enumerated at pass start and not yet walked. Null when no
    // pass is running (the next invocation re-enumerates spaces and starts a fresh pass). Only
    // touched by ResolveWorkspaceListsAsync — the refresh loop runs fetches strictly one at a time.
    private Queue<NamedEntity>? _walkPendingSpaces;

    // Every list the walk has discovered this session, keyed by id (last-wins on rename). The
    // mutable map is walk-owned; _knownWorkspaceLists republishes an immutable snapshot after each
    // step so other threads (a future New Task list picker, #208-J) read a coherent set.
    private readonly Dictionary<string, NamedEntity> _knownListsById = new(StringComparer.Ordinal);
    private volatile IReadOnlyList<NamedEntity> _knownWorkspaceLists = [];

    /// <summary>Every workspace list discovered so far by <see cref="ResolveWorkspaceListsAsync"/>
    /// (#236), deduped by id. Grows as walk steps land; empty until the first step. Safe to read
    /// from any thread.</summary>
    public IReadOnlyList<NamedEntity> KnownWorkspaceLists => _knownWorkspaceLists;

    /// <summary>
    /// Runs one bounded step of the workspace list-hierarchy walk (#236): on the first call of a
    /// pass, enumerates the workspace's spaces; each call then walks up to
    /// <see cref="MaxSpacesPerWalkStep"/> of them (folderless lists + per-folder lists) and
    /// accumulates the results into <see cref="KnownWorkspaceLists"/>. Returns
    /// <see cref="WorkspaceListsResolution.PassComplete"/> when every space has been covered — the
    /// caller stamps its cadence gate then, so the walk stays due (and keeps resuming) across
    /// cycles mid-pass, and the <em>next</em> pass starts fresh after the gate's minimum age
    /// (#246 ADR: mark ran at completion). Best-effort per space: a space whose walk fails is
    /// skipped for this pass rather than failing the step.
    /// </summary>
    public async Task<WorkspaceListsResolution> ResolveWorkspaceListsAsync(CancellationToken ct = default)
    {
        _walkPendingSpaces ??= new Queue<NamedEntity>(await client.GetSpacesAsync(config.WorkspaceId, ct));

        var step = new List<NamedEntity>();
        while (step.Count < MaxSpacesPerWalkStep && _walkPendingSpaces.Count > 0)
            step.Add(_walkPendingSpaces.Dequeue());

        var found = new System.Collections.Concurrent.ConcurrentBag<NamedEntity>();
        await Parallel.ForEachAsync(
            step,
            new ParallelOptions { MaxDegreeOfParallelism = MaxFanOutConcurrency, CancellationToken = ct },
            async (space, token) =>
            {
                try
                {
                    // Within a space the calls run serially — concurrency comes from walking the
                    // step's spaces side by side, so the step's in-flight ceiling is the step size,
                    // comfortably under MaxFanOutConcurrency and the process-wide gate (#193).
                    foreach (var list in await client.GetFolderlessListsAsync(space.Id, token))
                        found.Add(list);
                    foreach (var folder in await client.GetFoldersAsync(space.Id, token))
                        foreach (var list in await client.GetListsInFolderAsync(folder.Id, token))
                            found.Add(list);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    // Best-effort (#236): a space we can't walk contributes nothing this pass; the
                    // next pass retries it. That includes an HttpClient timeout — a cancellation-typed
                    // exception with our token unsignalled (RefreshService filters the same trap) —
                    // which must not abort the whole step and drop the other spaces' results. Only a
                    // genuine cancellation (token signalled: shutdown) propagates.
                }
            });

        foreach (var list in found)
            _knownListsById[list.Id] = list;
        _knownWorkspaceLists = _knownListsById.Values.ToList();

        var passComplete = _walkPendingSpaces.Count == 0;
        if (passComplete)
            _walkPendingSpaces = null; // next invocation starts a fresh pass (re-enumerating spaces)
        return new WorkspaceListsResolution(_knownWorkspaceLists, passComplete);
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
    /// see <c>docs/plans/completed/adaptive-subtask-fetch.md</c>.
    /// </para>
    /// Worst cases are bounded: the plan caps the whole-list and per-parent <em>seeds</em>, and the
    /// per-parent BFS below counts <em>every</em> <c>GetSubtasksAsync</c> round-trip (seeds + recursion)
    /// against <see cref="SubtaskFetchOptions.MaxPerParentFetches"/> so a deep/wide foreign subtree can't
    /// blow the budget. Any cap that drops work sets <see cref="ForeignSubtaskResolution.Truncated"/> so
    /// the caller can surface it (the TUI appends a note to the post-refresh status line) rather than
    /// truncating silently. The per-parent BFS fetches each level's parents concurrently under the shared
    /// <see cref="MaxFanOutConcurrency"/> cap (#144), matching the sibling fan-outs
    /// (<see cref="ResolveContextParentsAsync"/>, <see cref="ResolveListColorsAsync"/>).
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
        //
        // The BFS runs one level at a time, fetching a level's parents concurrently under the shared
        // MaxFanOutConcurrency cap (#144, follow-up to #87 — the per-parent path was serial, so it scaled
        // linearly with the number of foreign parents). A plain FIFO queue already visited the tree in
        // level order (all seeds, then their children), so this is behaviourally equivalent in which ids
        // are fetched and their cross-level round-trip order — it only overlaps the calls within a level.
        // Each level's results are merged single-threaded in level order, so `fetched` never sees a
        // concurrent write and the next frontier is deterministic.
        var budget = opts.MaxPerParentFetches;
        var spent = 0;
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(MaxFanOutConcurrency);
        // plan.PerParentIds is already distinct (SubtaskFetchStrategy dedups parents); the per-level
        // guards below handle any duplicates that recursion could surface.
        var frontier = plan.PerParentIds.ToList();
        while (frontier.Count > 0)
        {
            // Ids already expanded (a parent recursion surfaced again) cost no budget, matching the old
            // expanded-set guard; dedup within the level too. A child a whole-list fetch already pooled
            // is instead dropped at merge time by fetched.TryAdd, so it never enters the frontier.
            var level = new List<string>();
            var levelSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in frontier)
            {
                if (!expanded.Contains(id) && levelSeen.Add(id))
                    level.Add(id);
            }
            if (level.Count == 0)
                break;

            // Spend at most the remaining budget on this level, in stable order; a level that overflows
            // the budget drops its tail — pending work we won't reach this refresh, exactly the old
            // `spent >= budget` break — and flags truncation.
            if (spent + level.Count > budget)
            {
                level = level.Take(Math.Max(0, budget - spent)).ToList();
                truncated = true;
            }
            if (level.Count == 0)
                break; // budget exhausted with work still pending (truncated already set)

            foreach (var id in level)
                expanded.Add(id);
            spent += level.Count;

            // Fetch the level concurrently, bounded by the gate, preserving order so the merge below is
            // deterministic. Best-effort: a parent whose fetch throws contributes no children.
            var childLists = await Task.WhenAll(level.Select(async id =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    return await client.GetSubtasksAsync(id, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return (IReadOnlyList<TaskItem>)[];
                }
                finally
                {
                    gate.Release();
                }
            }));

            // Merge single-threaded in level order; newly-pooled children become the next frontier
            // (their own subtasks may be foreign too).
            var next = new List<string>();
            foreach (var children in childLists)
            {
                foreach (var child in children)
                {
                    if (string.IsNullOrEmpty(child.Id) || !fetched.TryAdd(child.Id, child))
                        continue;
                    next.Add(child.Id);
                }
            }
            frontier = next;
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
