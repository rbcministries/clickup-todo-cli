using System.Globalization;
using System.Text.Json;
using ClickUpTodo.ClickUp.Generated;
using ClickUpTodo.ClickUp.Generated.Models;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Microsoft.Kiota.Serialization.Json;
using ApiException = Microsoft.Kiota.Abstractions.ApiException;

namespace ClickUpTodo.ClickUp;

/// <summary>
/// Domain-facing facade over the Kiota-generated <see cref="ClickUpApiClient"/>. Handles auth and
/// paging and maps the generated models into the app's stable <see cref="TaskItem"/> /
/// <see cref="StatusOption"/> / <see cref="NamedEntity"/> records, so the TUI never sees generated types.
/// </summary>
public sealed class ClickUpClient : IClickUpClient, IDisposable
{
    private const int PageSize = 100; // ClickUp returns at most 100 tasks per page.

    private readonly HttpClientRequestAdapter _adapter;
    private readonly ClickUpApiClient _client;

    // Set when the caller hands over HttpClient ownership: the Kiota adapter only disposes a client
    // it created itself, so a factory-built pipeline would otherwise leak its connection pool (and
    // the rate-limit governor's semaphore) past ClickUpClient.Dispose.
    private readonly HttpClient? _ownedHttpClient;

    /// <summary>Drives the client with any Kiota auth provider (personal token or OAuth).
    /// Pass <paramref name="ownsHttpClient"/> when this client should dispose
    /// <paramref name="httpClient"/> along with itself (e.g. a pipeline the factory built for it).</summary>
    public ClickUpClient(IAuthenticationProvider authProvider, HttpClient? httpClient = null, bool ownsHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(authProvider);
        _adapter = new HttpClientRequestAdapter(authProvider, httpClient: httpClient);
        _client = new ClickUpApiClient(_adapter);
        _ownedHttpClient = ownsHttpClient ? httpClient : null;
    }

    /// <summary>Drives the client with a ClickUp personal API token (sent as a raw header).</summary>
    public ClickUpClient(string token, HttpClient? httpClient = null, bool ownsHttpClient = false)
        : this(new ClickUpTokenAuthProvider(token), httpClient, ownsHttpClient)
    {
    }

    /// <summary>The signed-in user. Doubles as a cheap token-validation call.</summary>
    public Task<ClickUpUser> GetMeAsync(CancellationToken ct = default) => Guard("GetAuthorizedUser", async () =>
    {
        var user = (await _client.V2.User.GetAsync(cancellationToken: ct))?.User;
        return new ClickUpUser(
            user?.Id ?? 0,
            user?.Username ?? user?.Email ?? user?.Id?.ToString(CultureInfo.InvariantCulture) ?? "unknown");
    });

    public Task<IReadOnlyList<NamedEntity>> GetWorkspacesAsync(CancellationToken ct = default)
        => Guard("GetAuthorizedTeams", async () =>
            Named((await _client.V2.Team.GetAsync(cancellationToken: ct))?.Teams, t => t.Id, t => t.Name));

    /// <summary>
    /// The members of a Workspace — their ids, usernames, and emails — so a username/email typed in an
    /// <c>Assignee IS</c> filter can be resolved to an id for the server-side task fetch (#73). ClickUp's
    /// <c>GET /team</c> returns every team the user belongs to with its members inline, so this reuses
    /// that one call and picks the requested workspace (falling back to the only/first team).
    /// </summary>
    public Task<IReadOnlyList<WorkspaceMember>> GetWorkspaceMembersAsync(string workspaceId, CancellationToken ct = default)
        => Guard("GetAuthorizedTeams", async () =>
        {
            var teams = (await _client.V2.Team.GetAsync(cancellationToken: ct))?.Teams ?? [];
            var team = teams.FirstOrDefault(t => t.Id == workspaceId) ?? teams.FirstOrDefault();
            return MapMembers(team?.Members);
        });

    public Task<IReadOnlyList<NamedEntity>> GetSpacesAsync(string workspaceId, CancellationToken ct = default)
        => Guard("GetSpaces", async () =>
            Named((await _client.V2.Team[workspaceId].Space.GetAsync(cancellationToken: ct))?.Spaces, s => s.Id, s => s.Name));

    public Task<IReadOnlyList<NamedEntity>> GetFoldersAsync(string spaceId, CancellationToken ct = default)
        => Guard("GetFolders", async () =>
            Named((await _client.V2.Space[spaceId].Folder.GetAsync(cancellationToken: ct))?.Folders, f => f.Id, f => f.Name));

    public Task<IReadOnlyList<NamedEntity>> GetFolderlessListsAsync(string spaceId, CancellationToken ct = default)
        => Guard("GetFolderlessLists", async () =>
            Named((await _client.V2.Space[spaceId].List.GetAsync(cancellationToken: ct))?.Lists, l => l.Id, l => l.Name));

    public Task<IReadOnlyList<NamedEntity>> GetListsInFolderAsync(string folderId, CancellationToken ct = default)
        => Guard("GetLists", async () =>
            Named((await _client.V2.Folder[folderId].List.GetAsync(cancellationToken: ct))?.Lists, l => l.Id, l => l.Name));

    /// <summary>A single list's id and name — used to validate a directly-entered list id.</summary>
    public Task<NamedEntity> GetListAsync(string listId, CancellationToken ct = default)
        => Guard("GetList", async () =>
        {
            var list = await _client.V2.List[listId].GetAsync(cancellationToken: ct);
            return new NamedEntity(list?.Id ?? listId, list?.Name ?? "(unnamed list)");
        });

    /// <summary>
    /// A list's own color chip (ClickUp's <c>status.color</c>, e.g. <c>#e16b16</c>), or null when the
    /// list has no color set. ClickUp stores the list color under a field named <c>status</c> that the
    /// generated model doesn't map, so it's read defensively from Kiota's <see cref="IParsable"/>
    /// additional data; any shape we can't read yields null (callers fall back to a generated hue).
    /// </summary>
    public Task<string?> GetListColorAsync(string listId, CancellationToken ct = default)
        => Guard("GetList", async () =>
            ExtractListColor(await _client.V2.List[listId].GetAsync(cancellationToken: ct)));

    /// <summary>
    /// Pulls <c>status.color</c> out of a list's unmapped additional data. Kiota represents nested
    /// objects as <see cref="UntypedObject"/>; we also tolerate a raw <see cref="JsonElement"/> in case
    /// the serializer shape changes. Returns null (rather than throwing) for any unexpected shape.
    /// </summary>
    internal static string? ExtractListColor(global::ClickUpTodo.ClickUp.Generated.Models.List? list)
    {
        if (list?.AdditionalData is null || !list.AdditionalData.TryGetValue("status", out var raw) || raw is null)
            return null;

        var color = raw switch
        {
            UntypedObject obj => (obj.GetValue().TryGetValue("color", out var c) ? c : null) is UntypedString s
                ? s.GetValue()
                : null,
            JsonElement el when el.ValueKind == JsonValueKind.Object && el.TryGetProperty("color", out var c)
                => c.GetString(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(color) ? null : color;
    }

    /// <summary>The available statuses for a list's workflow, ordered by ClickUp's order index.</summary>
    public Task<IReadOnlyList<StatusOption>> GetListStatusesAsync(string listId, CancellationToken ct = default)
        => Guard("GetList", async () =>
        {
            var statuses = (await _client.V2.List[listId].GetAsync(cancellationToken: ct))?.Statuses ?? [];
            return (IReadOnlyList<StatusOption>)statuses
                .OrderBy(s => s.Orderindex ?? int.MaxValue)
                .Where(s => !string.IsNullOrWhiteSpace(s.StatusProp))
                .Select(s => new StatusOption(s.StatusProp!, s.Color))
                .ToList();
        });

    /// <summary>
    /// All open workspace tasks assigned to any of <paramref name="assigneeIds"/>, de-paged. An
    /// <b>empty</b> set omits the <c>assignees</c> filter entirely, so ClickUp returns tasks for
    /// everyone in the workspace — a deliberately broad (and slower) fetch the caller opts into by
    /// clearing the Assignee rule (#68).
    /// <para>
    /// <paramref name="updatedAfterMs"/> is an optional server-side <c>date_updated_gt</c> window
    /// (epoch ms): when set, only tasks updated after it are returned, so a busy workspace fetches a
    /// smaller set (#244 — the feed's look-back window). Null (the default) omits the filter, leaving
    /// today's full-set behaviour untouched. It composes with <paramref name="includeClosed"/> exactly
    /// as the delta path does (see <see cref="GetAssignedTasksDeltaAsync"/>): the two query params are
    /// independent.
    /// </para>
    /// </summary>
    public Task<List<TaskItem>> GetAssignedTasksAsync(string workspaceId, IReadOnlyList<long> assigneeIds, bool includeClosed = false, long? updatedAfterMs = null, CancellationToken ct = default)
        => Guard("GetFilteredTeamTasks", () => PageAsync(page =>
            _client.V2.Team[workspaceId].Task.GetAsync(cfg =>
            {
                if (assigneeIds.Count > 0)
                    cfg.QueryParameters.Assignees = assigneeIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray();
                cfg.QueryParameters.Page = page;
                // Open-only by default; the F12 "Show Completed" toggle (#178) flips this on so the server
                // returns closed-type tasks too. When off, closed-type tasks are dropped server-side (and
                // any that still arrive as subtask anchors are hidden client-side by TaskView).
                cfg.QueryParameters.IncludeClosed = includeClosed;
                cfg.QueryParameters.Subtasks = true;
                if (updatedAfterMs is { } after)
                    cfg.QueryParameters.DateUpdatedGt = after;
            }, ct), ct));

    /// <summary>
    /// Tasks on a specific list, de-paged, subtasks included. Open-only by default; set
    /// <paramref name="includeClosed"/> to also return closed tasks — the adaptive whole-list subtask
    /// fetch (#87) needs closed intermediates so an open descendant under a closed parent still chains up
    /// (matching <see cref="GetSubtasksAsync"/>, which keeps closed). Archived tasks are always dropped.
    /// </summary>
    public Task<List<TaskItem>> GetListTasksAsync(string listId, bool includeClosed = false, CancellationToken ct = default)
        => Guard("GetTasks", () => PageAsync(page =>
            _client.V2.List[listId].Task.GetAsync(cfg =>
            {
                cfg.QueryParameters.Page = page;
                cfg.QueryParameters.IncludeClosed = includeClosed;
                cfg.QueryParameters.Subtasks = true;
                cfg.QueryParameters.Archived = false;
            }, ct), ct));

    /// <summary>
    /// Delta variant of <see cref="GetAssignedTasksAsync"/> (#194): only tasks whose
    /// <c>date_updated</c> is after <paramref name="updatedAfterMs"/> (epoch ms), <b>including closed
    /// ones</b> — a task that closed since the watermark must appear in the delta so the merge can
    /// drop it from the snapshot rather than let it linger.
    /// </summary>
    public Task<List<TaskItem>> GetAssignedTasksDeltaAsync(
        string workspaceId, IReadOnlyList<long> assigneeIds, long updatedAfterMs, CancellationToken ct = default)
        => Guard("GetFilteredTeamTasks", () => PageAsync(page =>
            _client.V2.Team[workspaceId].Task.GetAsync(cfg =>
            {
                if (assigneeIds.Count > 0)
                    cfg.QueryParameters.Assignees = assigneeIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray();
                cfg.QueryParameters.Page = page;
                cfg.QueryParameters.IncludeClosed = true;
                cfg.QueryParameters.Subtasks = true;
                cfg.QueryParameters.DateUpdatedGt = updatedAfterMs;
            }, ct), ct));

    /// <summary>
    /// Delta variant of <see cref="GetListTasksAsync"/> (#194): only tasks on the list whose
    /// <c>date_updated</c> is after <paramref name="updatedAfterMs"/> (epoch ms), closed included (see
    /// <see cref="GetAssignedTasksDeltaAsync"/>). Archived rows are always dropped — which means an
    /// archive is <b>invisible</b> to a delta (the row just stops appearing) and the stale entry
    /// lingers until the caller's periodic full resync; see
    /// <see cref="Services.TaskService.LoadSnapshotAsync"/>.
    /// </summary>
    public Task<List<TaskItem>> GetListTasksDeltaAsync(string listId, long updatedAfterMs, CancellationToken ct = default)
        => Guard("GetTasks", () => PageAsync(page =>
            _client.V2.List[listId].Task.GetAsync(cfg =>
            {
                cfg.QueryParameters.Page = page;
                cfg.QueryParameters.IncludeClosed = true;
                cfg.QueryParameters.Subtasks = true;
                cfg.QueryParameters.Archived = false;
                cfg.QueryParameters.DateUpdatedGt = updatedAfterMs;
            }, ct), ct));

    /// <summary>
    /// Create a task in the list <paramref name="listId"/> from <paramref name="task"/> and return it
    /// mapped to the stable <see cref="TaskItem"/> (from ClickUp's created-task response — same shape as
    /// a list row) so the caller can insert it without a read-after-write. Only <c>name</c> is required;
    /// the optional fields are sent only when set — Kiota omits a null typed property (and a null
    /// collection), so an unset description/priority/due-date and an empty assignee set send no key,
    /// leaving ClickUp to apply its list defaults. <paramref name="task"/>'s <c>PriorityLevel</c> is
    /// ClickUp's importance level (1=Urgent … 4=Low); assignees are sent as a flat id array (the create
    /// endpoint's shape, unlike the add/rem of an update).
    /// </summary>
    public Task<TaskItem> CreateTaskAsync(string listId, NewTaskRequest task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (string.IsNullOrWhiteSpace(task.Name))
            throw new ArgumentException("A task name is required to create a task.", nameof(task));

        return Guard("CreateTask", async () =>
        {
            var request = new CreateTaskRequest
            {
                Name = task.Name,
                Description = string.IsNullOrEmpty(task.Description) ? null : task.Description,
                Assignees = task.Assignees is { Count: > 0 } ids ? ids.Select(id => (long?)id).ToList() : null,
                Priority = task.PriorityLevel,
                DueDate = task.DueDateMs,
            };
            var created = await _client.V2.List[listId].Task.PostAsync(request, cancellationToken: ct)
                ?? throw new InvalidOperationException($"ClickUp returned no task for the create in list '{listId}'.");
            return Map(created);
        });
    }

    /// <summary>
    /// Set a task's status. <paramref name="statusName"/> must be one of its list's statuses.
    /// Returns the <b>confirmed</b> status name from the write response (ClickUp's
    /// <c>PUT /task/{id}</c> returns the updated task), or null if the response omits it — so the
    /// caller can display the server-confirmed value without a read-after-write round-trip.
    /// </summary>
    public Task<string?> SetTaskStatusAsync(string taskId, string statusName, CancellationToken ct = default)
        => Guard("UpdateTask", async () =>
        {
            var updated = await _client.V2.Task[taskId].PutAsync(new UpdateTaskRequest { Status = statusName }, cancellationToken: ct);
            return updated?.Status?.StatusProp;
        });

    /// <summary>
    /// Set (or clear) a task's priority. <paramref name="priorityLevel"/> is ClickUp's importance level
    /// — 1=Urgent … 4=Low, lower = more urgent (see <see cref="ClickUpPriority"/>) — or <c>null</c> to
    /// clear the priority. Returns the <b>server-confirmed</b> effective level from the
    /// <c>PUT /task/{id}</c> response (null when cleared/unset), mirroring
    /// <see cref="SetTaskStatusAsync"/>'s return-the-truth shape.
    /// </summary>
    public Task<int?> SetTaskPriorityAsync(string taskId, int? priorityLevel, CancellationToken ct = default)
        => Guard("UpdateTask", async () =>
        {
            var request = new UpdateTaskRequest();
            if (priorityLevel is { } level)
                request.Priority = level;
            else
                // Kiota omits a null typed property, so a plain `Priority = null` would send an empty
                // body and leave the priority untouched. Force an explicit `"priority": null` (which
                // ClickUp reads as "clear") via the additional-data bag instead.
                request.AdditionalData["priority"] = null!;

            var updated = await _client.V2.Task[taskId].PutAsync(request, cancellationToken: ct);
            return ClickUpPriority.Level(updated?.Priority?.Id, updated?.Priority?.PriorityProp);
        });

    /// <summary>
    /// Set a task's <b>plain-text</b> description (ClickUp's <c>description</c> field, not
    /// <c>markdown_description</c>) so a read → edit → write → re-read round-trip is lossless for the
    /// plain text the detail view already surfaces (<see cref="MapDetail"/> reads <c>text_content</c>
    /// → <c>description</c>). Pass <c>""</c> to clear the description — Kiota writes a non-null string,
    /// so an explicit empty string is sent and ClickUp clears the field; a <c>null</c> argument is
    /// rejected up front (Kiota would omit a null typed property and the write would silently no-op).
    /// Returns the <b>server-confirmed</b> description from the <c>PUT /task/{id}</c> response —
    /// the same return-the-truth contract as <see cref="SetTaskStatusAsync"/>, with the
    /// <c>text_content</c>-preferred-over-<c>description</c> read-back matching <see cref="MapDetail"/>
    /// (so a cleared/whitespace description reads back as <c>null</c>, exactly as the detail view sees it).
    /// </summary>
    public Task<string?> SetTaskDescriptionAsync(string taskId, string description, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(description);
        return Guard("UpdateTask", async () =>
        {
            var updated = await _client.V2.Task[taskId].PutAsync(new UpdateTaskRequest { Description = description }, cancellationToken: ct);
            return !string.IsNullOrWhiteSpace(updated?.TextContent) ? updated!.TextContent : updated?.Description;
        });
    }

    /// <summary>
    /// Add a user to a task's assignees. ClickUp's <c>PUT /task/{id}</c> takes
    /// <c>assignees: { add: [...] }</c>. Returns the task's <b>reconciled</b> assignee set from the
    /// response so a caller can update the row without a read-after-write.
    /// </summary>
    public Task<IReadOnlyList<TaskAssignee>> AddTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default)
        => UpdateAssigneesAsync(taskId, add: userId, remove: null, ct);

    /// <summary>
    /// Remove a user from a task's assignees (ClickUp <c>assignees: { rem: [...] }</c>). Returns the
    /// task's reconciled assignee set from the response.
    /// </summary>
    public Task<IReadOnlyList<TaskAssignee>> RemoveTaskAssigneeAsync(string taskId, long userId, CancellationToken ct = default)
        => UpdateAssigneesAsync(taskId, add: null, remove: userId, ct);

    /// <summary>
    /// Shared body for the assignee add/remove writes: sends only the relevant side (Kiota omits the
    /// null collection, so <c>add</c>-only sends no <c>rem</c> and vice-versa) and maps the updated
    /// task's assignees back to the stable <see cref="TaskAssignee"/> shape.
    /// </summary>
    private Task<IReadOnlyList<TaskAssignee>> UpdateAssigneesAsync(string taskId, long? add, long? remove, CancellationToken ct)
        => Guard("UpdateTask", async () =>
        {
            var request = new UpdateTaskRequest
            {
                Assignees = new AssigneeUpdate
                {
                    Add = add is { } a ? [a] : null,
                    Rem = remove is { } r ? [r] : null,
                },
            };
            var updated = await _client.V2.Task[taskId].PutAsync(request, cancellationToken: ct);
            return MapAssignees(updated?.Assignees);
        });

    /// <summary>Full detail for a single task (description, tags, assignees, dates, custom fields).</summary>
    public Task<TaskDetail> GetTaskDetailAsync(string taskId, CancellationToken ct = default)
        => Guard("GetTask", async () =>
        {
            var t = await _client.V2.Task[taskId].GetAsync(cancellationToken: ct)
                    ?? throw new InvalidOperationException($"ClickUp returned no task for id '{taskId}'.");
            return MapDetail(t);
        });

    /// <summary>
    /// The direct subtasks of a task, regardless of assignee, mapped to the stable <see cref="TaskItem"/>
    /// shape (#70). Uses <c>GET /task/{id}?include_subtasks=true</c>; the archived ones are dropped to
    /// mirror the list/team fetches. The result carries each child's own <c>parent</c>, so the caller can
    /// recurse to gather deeper descendants. Returns an empty list when the task has no subtasks.
    /// </summary>
    public Task<IReadOnlyList<TaskItem>> GetSubtasksAsync(string taskId, CancellationToken ct = default)
        => Guard("GetTask", async () =>
        {
            var t = await _client.V2.Task[taskId].GetAsync(cfg => cfg.QueryParameters.IncludeSubtasks = true, ct);
            return (IReadOnlyList<TaskItem>)(t?.Subtasks?
                .Where(s => s.Archived != true)
                .Select(Map)
                .ToList() ?? []);
        });

    /// <summary>
    /// The comments on a task, <b>de-paged</b>, mapped to the stable <see cref="CommentItem"/> shape and
    /// stamped with <paramref name="taskId"/> so a caller aggregating comments across tasks (the feed,
    /// #109) can attribute each one. ClickUp returns comments most-recent-first, 25 per page, and
    /// paginates by a <c>start</c>/<c>start_id</c> cursor rather than a page number;
    /// <see cref="DePageCommentsAsync"/> walks that cursor to gather a busy task's full history, bounded
    /// at <see cref="MaxCommentPages"/> pages (~1000 comments) so a looping cursor can't fetch forever.
    /// </summary>
    public Task<IReadOnlyList<CommentItem>> GetTaskCommentsAsync(string taskId, CancellationToken ct = default)
        => Guard("GetTaskComments", async () =>
        {
            var comments = await DePageCommentsAsync(
                (cursor, token) => _client.V2.Task[taskId].Comment.GetAsync(cfg =>
                {
                    if (cursor is { } c)
                    {
                        cfg.QueryParameters.Start = c.Start;
                        cfg.QueryParameters.StartId = c.StartId;
                    }
                }, token),
                // No app logger reaches this facade, and writing to the console would corrupt the TUI —
                // so surface the (unrealistic, ~1000-comment) cap via a diagnostic trace rather than
                // silently dropping older history (#130). Harmless with no trace listener attached.
                onCapReached: count => System.Diagnostics.Trace.TraceWarning(
                    $"ClickUp task '{taskId}': comment history capped at {count} comments " +
                    $"({MaxCommentPages} pages); older comments were not fetched."),
                ct);
            return (IReadOnlyList<CommentItem>)comments.Select(c => MapComment(c, taskId)).ToList();
        });

    /// <summary>
    /// Post a <b>plain-text</b> comment to a task (<c>POST /task/{id}/comment</c>, #210) and return it
    /// as a <see cref="CommentItem"/> so a caller (the #216 composer) can append it optimistically.
    /// Rich content — @-mentions, task links, other entity tagging — is out of scope (a later epic);
    /// only <c>comment_text</c> is sent. <c>notify_all</c> is sent as <c>false</c>.
    /// <para>
    /// ClickUp's create-comment response is <b>minimal</b> — it returns only the new comment's
    /// <c>id</c>, a <c>hist_id</c>, and the server <c>date</c> (epoch ms); it does <b>not</b> echo the
    /// text, author, or structured blocks. So <see cref="MapComment"/> (which reads those off a full
    /// <see cref="Comment"/>) can't recover them here. The returned <see cref="CommentItem"/> is built
    /// from the response <c>id</c>/<c>date</c> plus the <paramref name="text"/> we just posted (lossless
    /// for plain text); <see cref="CommentItem.Author"/> is left empty for the caller's optimistic row
    /// to stamp (it knows the current user) and is reconciled on the next comment fetch.
    /// </para>
    /// </summary>
    public Task<CommentItem> CreateTaskCommentAsync(string taskId, string text, CancellationToken ct = default)
    {
        // ClickUp rejects an empty comment_text with a 400; fail faster and clearer at the boundary.
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return Guard("CreateTaskComment", async () =>
        {
            var request = new CreateCommentRequest { CommentText = text, NotifyAll = false };
            var created = await _client.V2.Task[taskId].Comment.PostAsync(request, cancellationToken: ct);
            return new CommentItem(
                Id: created?.Id ?? "",
                Author: "",
                DateMs: created?.Date,
                Text: text,
                Resolved: false,
                TaskId: taskId);
        });
    }

    // ── Mapping & plumbing ──────────────────────────────────────────────────

    // internal (not private) so the mapping can be unit-tested without hitting the live API.
    internal static TaskItem Map(TaskObject t)
    {
        var priorityLevel = ClickUpPriority.Level(t.Priority?.Id, t.Priority?.PriorityProp);
        return new()
        {
            Id = t.Id ?? "",
            CustomId = t.CustomId,
            Name = t.Name ?? "(untitled)",
            Url = t.Url,
            ParentId = string.IsNullOrWhiteSpace(t.Parent) ? null : t.Parent,
            DueDateMs = ParseMs(t.DueDate),
            CreatedMs = ParseMs(t.DateCreated),
            UpdatedMs = ParseMs(t.DateUpdated),
            ListId = t.List?.Id,
            ListName = t.List?.Name,
            StatusName = t.Status?.StatusProp,
            StatusColor = t.Status?.Color,
            StatusType = t.Status?.Type,
            PriorityLevel = priorityLevel,
            PriorityName = ClickUpPriority.NameFromLevel(priorityLevel),
            PriorityColor = t.Priority?.Color,
            Assignees = MapAssignees(t.Assignees),
        };
    }

    /// <summary>Maps ClickUp's <c>assignees</c> to the stable <see cref="TaskAssignee"/> shape, keeping
    /// the numeric id (for matching / the app user) and a display name; drops entries with neither.</summary>
    private static IReadOnlyList<TaskAssignee> MapAssignees(List<User>? assignees)
        => assignees?
            .Select(u => new TaskAssignee(u.Id ?? 0, DisplayName(u)))
            .Where(a => a.Id != 0 || a.Name.Length > 0)
            .ToList()
           ?? [];

    /// <summary>Maps a Workspace's <c>members</c> (each wrapped as <c>{ user }</c>) to the stable
    /// <see cref="WorkspaceMember"/> shape, keeping id/username/email; drops entries with no id (an id is
    /// required to resolve a name/email into an <c>assignees[]</c> filter). internal for unit testing.</summary>
    internal static IReadOnlyList<WorkspaceMember> MapMembers(List<Member>? members)
        => members?
            .Select(m => new WorkspaceMember(m.User?.Id ?? 0, m.User?.Username, m.User?.Email))
            .Where(m => m.Id != 0)
            .ToList()
           ?? [];

    /// <summary>
    /// Maps a generated <see cref="Comment"/> onto the stable <see cref="CommentItem"/>, stamping the
    /// owning <paramref name="taskId"/> for feed attribution (#111). Author uses the same
    /// username → email → id fallback as task assignees; a missing/unparseable date yields a null
    /// <see cref="CommentItem.DateMs"/>. The structured <c>comment</c> blocks are scanned for @-mention
    /// runs and their referenced member ids surfaced as <see cref="CommentItem.MentionedUserIds"/> (#167).
    /// internal (not private) so it can be unit-tested offline.
    /// </summary>
    internal static CommentItem MapComment(Comment c, string? taskId) => new(
        Id: c.Id ?? "",
        Author: DisplayName(c.User),
        DateMs: ParseMs(c.Date),
        Text: c.CommentText ?? "",
        Resolved: c.Resolved == true,
        TaskId: taskId,
        MentionedUserIds: MapMentionedUserIds(c.CommentProp));

    /// <summary>Extracts the distinct numeric ids of members @-mentioned in a comment's structured
    /// blocks — the runs carrying a <c>user</c> with a positive id (a mention/tag block, per #167).
    /// Plain-text runs and blocks with no/zero user id contribute nothing; a null blocks array yields an
    /// empty list. internal for offline unit testing.</summary>
    internal static IReadOnlyList<long> MapMentionedUserIds(List<CommentBlock>? blocks)
        => blocks?
            .Select(b => b.User?.Id ?? 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList()
           ?? [];

    // internal (not private) so the mapping can be unit-tested without hitting the live API.
    internal static TaskDetail MapDetail(TaskObject t) => new()
    {
        Id = t.Id ?? "",
        CustomId = t.CustomId,
        Name = t.Name ?? "(untitled)",
        Url = t.Url,
        StatusName = t.Status?.StatusProp,
        StatusColor = t.Status?.Color,
        ListId = t.List?.Id,
        ListName = t.List?.Name,
        Lists = t.Locations?
            .Select(l => new NamedEntity(l.Id ?? "", l.Name ?? ""))
            .Where(l => !string.IsNullOrWhiteSpace(l.Name))
            .ToList() ?? [],
        // ClickUp's text_content is the rendered plain text; description is the raw (often markdown)
        // source. Prefer the plain text for a terminal, falling back to the raw form.
        Description = !string.IsNullOrWhiteSpace(t.TextContent) ? t.TextContent : t.Description,
        Priority = t.Priority?.PriorityProp,
        PriorityColor = t.Priority?.Color,
        DueDateMs = ParseMs(t.DueDate),
        CreatedMs = ParseMs(t.DateCreated),
        UpdatedMs = ParseMs(t.DateUpdated),
        Tags = t.Tags?.Select(tag => tag.Name ?? "").Where(n => n.Length > 0).ToList() ?? [],
        Assignees = t.Assignees?.Select(DisplayName).Where(n => n.Length > 0).ToList() ?? [],
        CustomFields = t.CustomFields?
            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .Select(MapCustomField)
            .ToList() ?? [],
    };

    /// <summary>
    /// Maps a generated <see cref="CustomField"/> onto the stable <see cref="CustomFieldItem"/>,
    /// including its loosely-typed <c>value</c> and <c>type_config.options</c>. The generated type
    /// only surfaces <c>id</c>/<c>name</c>/<c>type</c>; the rest lands in Kiota's <c>AdditionalData</c>
    /// as mixed boxed types, so we re-serialize the field to JSON (a faithful round-trip) and read
    /// the value/options back with <see cref="System.Text.Json"/> — no dependency on the internal
    /// <c>UntypedNode</c> shape and no generated type escaping this facade (issue #35).
    /// </summary>
    internal static CustomFieldItem MapCustomField(CustomField f)
    {
        try
        {
            var (value, options) = CustomFieldReader.Read(SerializeToJson(f));
            return new CustomFieldItem(f.Name!, f.Type, value, options);
        }
        catch
        {
            // One malformed/unexpected field must never sink the whole task's detail — degrade to
            // name/type only (the same shape the tab showed before values were surfaced).
            return new CustomFieldItem(f.Name!, f.Type);
        }
    }

    /// <summary>Serializes any Kiota model to a detached <see cref="JsonElement"/>. Uses the JSON
    /// writer factory directly (no reliance on global serializer registration), and clones the root
    /// so it outlives the backing <see cref="JsonDocument"/>.</summary>
    private static JsonElement SerializeToJson(IParsable value)
    {
        using var writer = new JsonSerializationWriterFactory().GetSerializationWriter("application/json");
        // WriteObjectValue (not value.Serialize) so the writer opens/closes the root JSON object.
        writer.WriteObjectValue(null, value);
        using var stream = writer.GetSerializedContent();
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }

    /// <summary>Best display name for a user: username, then email, then numeric id.</summary>
    private static string DisplayName(User? user)
        => user is null
            ? ""
            : !string.IsNullOrWhiteSpace(user.Username) ? user.Username!
            : !string.IsNullOrWhiteSpace(user.Email) ? user.Email!
            : user.Id?.ToString(CultureInfo.InvariantCulture) ?? "";

    /// <summary>Parses a ClickUp epoch-milliseconds string, or null when absent/unparseable.</summary>
    private static long? ParseMs(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? ms : null;

    /// <summary>Walks a paginated task endpoint until ClickUp reports the last page.</summary>
    private static async Task<List<TaskItem>> PageAsync(Func<int, Task<TasksResponse?>> fetchPage, CancellationToken ct)
    {
        var all = new List<TaskItem>();
        for (var page = 0; ; page++)
        {
            ct.ThrowIfCancellationRequested();
            var resp = await fetchPage(page);
            var tasks = resp?.Tasks;
            if (tasks is { Count: > 0 })
                all.AddRange(tasks.Where(t => t.Archived != true).Select(Map));

            // Stop on last_page, or when a short/empty page implies there's no more.
            if (resp?.LastPage == true || tasks is null || tasks.Count < PageSize)
                break;
        }
        return all;
    }

    // ClickUp's GET /task/{id}/comment returns at most 25 comments per page, most-recent-first.
    private const int CommentPageSize = 25;

    // Hard cap on comment pages walked, so a stuck/looping cursor can never fetch unbounded history.
    // 40 × 25 ⇒ up to ~1000 comments; reaching it fires the onCapReached seam rather than truncating
    // silently (#130). No realistic feed use (#112) needs a single task's entire history beyond this.
    private const int MaxCommentPages = 40;

    /// <summary>The next-page cursor for ClickUp's comment endpoint: the epoch-ms <c>date</c> and
    /// <c>id</c> of the oldest comment received so far, passed back as <c>start</c>/<c>start_id</c> to
    /// page toward older comments.</summary>
    internal readonly record struct CommentCursor(long Start, string StartId);

    /// <summary>
    /// Derives the next-page cursor from a page of comments. Comments arrive most-recent-first, so the
    /// oldest — the anchor for the next (older) page — is the last element; this scans from the end for
    /// the first entry with a non-empty id and a parseable epoch-ms date. Returns null when the page is
    /// empty or nothing qualifies, so the caller stops rather than re-paging on a bad cursor.
    /// </summary>
    internal static CommentCursor? NextCommentCursor(IReadOnlyList<Comment> page)
    {
        for (var i = page.Count - 1; i >= 0; i--)
        {
            var id = page[i].Id;
            if (!string.IsNullOrEmpty(id) && ParseMs(page[i].Date) is { } start)
                return new CommentCursor(start, id);
        }
        return null;
    }

    /// <summary>
    /// Walks ClickUp's cursor-paginated comment endpoint (most-recent-first, <see cref="CommentPageSize"/>
    /// per page) until a task's whole history is gathered. Unlike page-number-driven <see cref="PageAsync"/>,
    /// it threads the <see cref="NextCommentCursor"/> (<c>start</c>/<c>start_id</c>) between calls. Comments
    /// are de-duped by id — a boundary cursor can re-return its anchor — and the walk stops on a
    /// short/empty page, when a full page adds nothing new (a stuck cursor), or at the
    /// <see cref="MaxCommentPages"/> cap. Reaching the cap invokes <paramref name="onCapReached"/> (with the
    /// count gathered) so the truncation is observable, never silent (#130). The page fetch is injected so
    /// the loop is unit-testable offline against constructed responses.
    /// </summary>
    internal static async Task<List<Comment>> DePageCommentsAsync(
        Func<CommentCursor?, CancellationToken, Task<CommentsResponse?>> fetchPage,
        Action<int>? onCapReached,
        CancellationToken ct)
    {
        var all = new List<Comment>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        CommentCursor? cursor = null;

        for (var pagesFetched = 1; ; pagesFetched++)
        {
            ct.ThrowIfCancellationRequested();
            var page = (await fetchPage(cursor, ct))?.Comments ?? [];

            var madeProgress = false;
            foreach (var c in page)
            {
                if (string.IsNullOrEmpty(c.Id))
                {
                    // A blank id can't be de-duped or anchor a cursor. Keep the content, but it doesn't
                    // count as progress — else a stuck cursor re-returning a page that holds one blank-id
                    // comment would fool the guard below and run all the way to the cap.
                    all.Add(c);
                }
                else if (seenIds.Add(c.Id!))
                {
                    all.Add(c);
                    madeProgress = true;
                }
            }

            // Last page (short/empty), or a full page that surfaced no new id'd comment (cursor stuck) ⇒ done.
            if (page.Count < CommentPageSize || !madeProgress)
                break;

            if (pagesFetched >= MaxCommentPages)
            {
                onCapReached?.Invoke(all.Count);
                break;
            }

            cursor = NextCommentCursor(page);
            if (cursor is null)
                break; // couldn't derive a cursor from a full page ⇒ stop rather than refetch page 0.
        }

        return all;
    }

    private static IReadOnlyList<NamedEntity> Named<T>(List<T>? items, Func<T, string?> id, Func<T, string?> name)
        => items?.Select(i => new NamedEntity(id(i) ?? "", name(i) ?? "(unnamed)"))
                 .Where(e => !string.IsNullOrEmpty(e.Id))
                 .ToList()
           ?? [];

    /// <summary>Runs a generated call, translating Kiota <see cref="ApiException"/> into our own type.</summary>
    private static async Task<T> Guard<T>(string operation, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (ApiException ex)
        {
            throw new ClickUpApiException(ex.ResponseStatusCode, operation, ex);
        }
    }

    public void Dispose()
    {
        _adapter.Dispose();
        _ownedHttpClient?.Dispose();
    }
}
