using System.Globalization;
using System.Text.Json;
using ClickUpTodo.ClickUp.Generated;
using ClickUpTodo.ClickUp.Generated.Models;
using ClickUpTodo.Configuration;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Microsoft.Kiota.Serialization.Json;
using ApiException = Microsoft.Kiota.Abstractions.ApiException;
// The generated field-definition model shares its name with the stable domain record
// (ClickUpTodo.ClickUp.CustomFieldDefinition); alias the generated one so the facade can name both.
using GenCustomFieldDefinition = ClickUpTodo.ClickUp.Generated.Models.CustomFieldDefinition;

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

    // The cross-process nudge channel (#294): after a confirmed (2xx) write the facade records a
    // change marker here so other running instances can re-fetch the changed task. Defaults to a no-op
    // so a caller that doesn't wire multi-tab support (or the offline write tests) behaves exactly as
    // before. Never null — the write paths call it unconditionally.
    private readonly IChangeMarkerStore _changeMarkers;

    // Set when the caller hands over HttpClient ownership: the Kiota adapter only disposes a client
    // it created itself, so a factory-built pipeline would otherwise leak its connection pool (and
    // the rate-limit governor's semaphore) past ClickUpClient.Dispose.
    private readonly HttpClient? _ownedHttpClient;

    /// <summary>Drives the client with any Kiota auth provider (personal token or OAuth).
    /// Pass <paramref name="ownsHttpClient"/> when this client should dispose
    /// <paramref name="httpClient"/> along with itself (e.g. a pipeline the factory built for it).
    /// <paramref name="changeMarkers"/> receives a nudge after each confirmed write (#294); omit it
    /// (or pass null) to disable the channel.</summary>
    public ClickUpClient(
        IAuthenticationProvider authProvider, HttpClient? httpClient = null, bool ownsHttpClient = false,
        IChangeMarkerStore? changeMarkers = null)
    {
        ArgumentNullException.ThrowIfNull(authProvider);
        _adapter = new HttpClientRequestAdapter(authProvider, httpClient: httpClient);
        _client = new ClickUpApiClient(_adapter);
        _ownedHttpClient = ownsHttpClient ? httpClient : null;
        _changeMarkers = changeMarkers ?? NullChangeMarkerStore.Instance;
    }

    /// <summary>Drives the client with a ClickUp personal API token (sent as a raw header).</summary>
    public ClickUpClient(
        string token, HttpClient? httpClient = null, bool ownsHttpClient = false,
        IChangeMarkerStore? changeMarkers = null)
        : this(new ClickUpTokenAuthProvider(token), httpClient, ownsHttpClient, changeMarkers)
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
    /// The Custom Field <b>definitions</b> accessible from a list (<c>GET /list/{list_id}/field</c>,
    /// #249): each field's id, name, type, <c>required</c> flag, and drop-down/label options. This is the
    /// schema side (what fields a list has and how to render an input for each) — a task's values come
    /// back separately on <see cref="TaskDetail.CustomFields"/>. Fields with a blank id are dropped (an
    /// id is required to write the value back). Maps onto the stable <see cref="CustomFieldDefinition"/>
    /// so no generated type escapes the facade.
    /// </summary>
    public Task<IReadOnlyList<CustomFieldDefinition>> GetListCustomFieldsAsync(string listId, CancellationToken ct = default)
        => Guard("GetListCustomFields", async () =>
        {
            var fields = (await _client.V2.List[listId].Field.GetAsync(cancellationToken: ct))?.Fields ?? [];
            return (IReadOnlyList<CustomFieldDefinition>)fields
                .Where(f => !string.IsNullOrWhiteSpace(f.Id))
                .Select(MapCustomFieldDefinition)
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
    /// endpoint's shape, unlike the add/rem of an update). Any <see cref="NewTaskRequest.CustomFields"/>
    /// (#368) are sent as ClickUp's <c>custom_fields: [{ id, value }]</c> array (loosely typed, so carried
    /// on <c>AdditionalData</c> as an <see cref="UntypedNode"/> tree — no spec change); an empty set sends
    /// no key.
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
            // Custom-field values (#368) are loosely typed, so they ride on AdditionalData as a Kiota
            // UntypedNode tree (no spec change / no regen — see docs/plans/completed/new-task-custom-field-values.md)
            // rather than a rigid generated property. Empty ⇒ no key, leaving today's create body untouched.
            if (task.CustomFields is { Count: > 0 } customFields)
            {
                request.AdditionalData["custom_fields"] = new UntypedArray(customFields
                    .Where(f => !string.IsNullOrWhiteSpace(f.Id))
                    .Select(f => (UntypedNode)new UntypedObject(new Dictionary<string, UntypedNode>
                    {
                        ["id"] = new UntypedString(f.Id),
                        ["value"] = ToUntyped(f.Value),
                    })));
            }
            var created = await _client.V2.List[listId].Task.PostAsync(request, cancellationToken: ct)
                ?? throw new InvalidOperationException($"ClickUp returned no task for the create in list '{listId}'.");
            return Map(created);
        });
    }

    /// <summary>
    /// Permanently delete a task (<c>DELETE /task/{task_id}</c>). ClickUp returns an empty body. Errors
    /// surface as a caught <see cref="ClickUpApiException"/>. The app has no delete UI; this exists so the
    /// create-task integration test can remove its throwaway task and stay idempotent.
    /// </summary>
    public Task DeleteTaskAsync(string taskId, CancellationToken ct = default)
        => Guard("DeleteTask", async () =>
        {
            using var _ = await _client.V2.Task[taskId].DeleteAsync(cancellationToken: ct);
        });

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
            // Reached only on a 2xx (a non-2xx throws above), so this is the confirmed-write nudge (#294).
            Nudge(taskId, updated, StatusFields);
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
            Nudge(taskId, updated, PriorityFields);
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
            Nudge(taskId, updated, DescriptionFields);
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
            Nudge(taskId, updated, AssigneeFields);
            return MapAssignees(updated?.Assignees);
        });

    /// <summary>
    /// Add a task to an <b>additional</b> list — ClickUp's "Tasks in Multiple Lists" feature
    /// (<c>POST /list/{list_id}/task/{task_id}</c>). The task keeps its home list (set at creation)
    /// and gains <paramref name="listId"/> as an extra location, surfaced by
    /// <see cref="GetTaskDetailAsync"/> in <see cref="TaskDetail.Lists"/>. ClickUp returns an empty
    /// body, so this returns no value.
    /// <para><b>Workspace prerequisite:</b> "Tasks in Multiple Lists" is an opt-in ClickApp; when it is
    /// disabled the call fails with an HTTP 4xx (ClickUp error <c>OV_016</c>), surfaced here as a caught
    /// <see cref="ClickUpApiException"/> so a caller (e.g. the New Task screen #241 or Quick Updates
    /// #242) can flash it rather than crash.</para>
    /// </summary>
    public Task AddTaskToListAsync(string taskId, string listId, CancellationToken ct = default)
        => Guard("AddTaskToList", async () =>
        {
            using var _ = await _client.V2.List[listId].Task[taskId].PostAsync(cancellationToken: ct);
            // A membership change alters what another tab's list-membership views show, so nudge (#348).
            // The endpoint returns an empty body (no date_updated), so the marker carries a null server
            // date — the consumer simply always re-fetches on a lists nudge, same as the comment case.
            _changeMarkers.Record(taskId, serverDateUpdatedMs: null, ListsFields);
        });

    /// <summary>
    /// Remove a task from an additional list (<c>DELETE /list/{list_id}/task/{task_id}</c>) — the
    /// inverse of <see cref="AddTaskToListAsync"/>. The task's home list is unaffected; ClickUp returns
    /// an empty body. Errors surface as a caught <see cref="ClickUpApiException"/>.
    /// </summary>
    public Task RemoveTaskFromListAsync(string taskId, string listId, CancellationToken ct = default)
        => Guard("RemoveTaskFromList", async () =>
        {
            using var _ = await _client.V2.List[listId].Task[taskId].DeleteAsync(cancellationToken: ct);
            // Inverse of AddTaskToListAsync — nudge on the confirmed removal too (#348). Empty body ⇒
            // null server date (consumer always re-fetches on a lists nudge).
            _changeMarkers.Record(taskId, serverDateUpdatedMs: null, ListsFields);
        });

    /// <summary>
    /// Toggle (or set) a checklist item's <c>resolved</c> state (D, #457) via
    /// <c>PUT /checklist/{checklist_id}/checklist_item/{checklist_item_id}</c> with a <c>{ resolved }</c>
    /// body. ClickUp echoes the whole parent checklist, so this returns the <b>server-confirmed</b>
    /// <see cref="TaskChecklist"/> — mapped through the same <see cref="MapChecklist"/> as the read path,
    /// so its items come back through <see cref="ChecklistReader"/> and no generated type escapes the
    /// facade. Same return-the-truth contract as <see cref="SetTaskStatusAsync"/> /
    /// <see cref="SetTaskDescriptionAsync"/>. (Multi-tab checklist nudge sync is left to a later slice —
    /// another tab's 30 s auto-refresh still picks the change up.)
    /// </summary>
    public Task<TaskChecklist> SetChecklistItemResolvedAsync(string checklistId, string itemId, bool resolved, CancellationToken ct = default)
        => Guard("UpdateChecklistItem", async () =>
        {
            var response = await _client.V2.Checklist[checklistId].Checklist_item[itemId]
                .PutAsync(new UpdateChecklistItemRequest { Resolved = resolved }, cancellationToken: ct);
            var checklist = response?.Checklist
                ?? throw new InvalidOperationException($"ClickUp returned no checklist for item '{itemId}'.");
            return MapChecklist(checklist);
        });

    /// <summary>
    /// Create a checklist item (E, #458) via <c>POST /checklist/{checklist_id}/checklist_item</c> with a
    /// <c>{ name }</c> body. ClickUp echoes the whole parent checklist (the new item included, with its
    /// server id/orderindex + refreshed counts), so this returns the <b>server-confirmed</b>
    /// <see cref="TaskChecklist"/> — mapped through the same <see cref="MapChecklist"/> as the read path,
    /// so no generated type escapes the facade. Same return-the-truth contract as
    /// <see cref="SetChecklistItemResolvedAsync"/>. Per-item assignee is G (#460); this sends name only.
    /// </summary>
    public Task<TaskChecklist> CreateChecklistItemAsync(string checklistId, string name, CancellationToken ct = default)
        => Guard("CreateChecklistItem", async () =>
        {
            var response = await _client.V2.Checklist[checklistId].Checklist_item
                .PostAsync(new CreateChecklistItemRequest { Name = name }, cancellationToken: ct);
            var checklist = response?.Checklist
                ?? throw new InvalidOperationException($"ClickUp returned no checklist for new item in '{checklistId}'.");
            return MapChecklist(checklist);
        });

    /// <summary>
    /// Rename a checklist item (E, #458) via <c>PUT /checklist/{checklist_id}/checklist_item/{checklist_item_id}</c>
    /// with a <c>{ name }</c> body (the same path/response as the D toggle, a different field). ClickUp
    /// echoes the whole parent checklist, so this returns the <b>server-confirmed</b>
    /// <see cref="TaskChecklist"/> mapped via <see cref="MapChecklist"/>.
    /// </summary>
    public Task<TaskChecklist> RenameChecklistItemAsync(string checklistId, string itemId, string name, CancellationToken ct = default)
        => Guard("UpdateChecklistItem", async () =>
        {
            var response = await _client.V2.Checklist[checklistId].Checklist_item[itemId]
                .PutAsync(new UpdateChecklistItemRequest { Name = name }, cancellationToken: ct);
            var checklist = response?.Checklist
                ?? throw new InvalidOperationException($"ClickUp returned no checklist for item '{itemId}'.");
            return MapChecklist(checklist);
        });

    /// <summary>
    /// Delete a checklist item (E, #458) via <c>DELETE /checklist/{checklist_id}/checklist_item/{checklist_item_id}</c>.
    /// ClickUp returns an empty body, so there is nothing to map — the caller keeps its optimistic local
    /// removal (revert-on-failure), exactly as <see cref="DeleteTaskAsync"/> does. Errors surface as a
    /// caught <see cref="ClickUpApiException"/>.
    /// </summary>
    public Task DeleteChecklistItemAsync(string checklistId, string itemId, CancellationToken ct = default)
        => Guard("DeleteChecklistItem", async () =>
        {
            using var _ = await _client.V2.Checklist[checklistId].Checklist_item[itemId].DeleteAsync(cancellationToken: ct);
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
    /// Full detail for a task addressed by its workspace <b>custom id</b> (#303) — the same
    /// <c>GET /task/{id}</c> endpoint with <c>custom_task_ids=true&amp;team_id={teamId}</c> so ClickUp
    /// resolves the custom id within the workspace. The response is a normal task, so the mapped
    /// <see cref="TaskDetail.Id"/> is the task's <b>plain</b> id — the quick-open host then opens it
    /// through the ordinary <see cref="GetTaskDetailAsync"/> path.
    /// </summary>
    public Task<TaskDetail> GetTaskDetailByCustomIdAsync(string customId, string teamId, CancellationToken ct = default)
        => Guard("GetTaskByCustomId", async () =>
        {
            var t = await _client.V2.Task[customId].GetAsync(cfg =>
            {
                cfg.QueryParameters.CustomTaskIds = true;
                cfg.QueryParameters.TeamId = teamId;
            }, ct) ?? throw new InvalidOperationException($"ClickUp returned no task for custom id '{customId}'.");
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
    /// A single task mapped to the stable <see cref="TaskItem"/> shape (#291): <c>GET /task/{id}</c> →
    /// <see cref="Map"/> (not <see cref="MapDetail"/>), so it carries <c>parent</c>, structured assignees,
    /// and field colours — everything the shared row renderer needs. Mirrors
    /// <see cref="GetTaskDetailAsync"/>'s fetch; the Task Tree tab (F, #291) uses it to walk a task's
    /// ancestry one parent at a time.
    /// </summary>
    public Task<TaskItem> GetTaskItemAsync(string taskId, CancellationToken ct = default)
        => Guard("GetTask", async () =>
        {
            var t = await _client.V2.Task[taskId].GetAsync(cancellationToken: ct)
                    ?? throw new InvalidOperationException($"ClickUp returned no task for id '{taskId}'.");
            return Map(t);
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
            // A comment bumps the task's date_updated, but the create-comment response returns only the
            // comment's own id/date, not the task's — so the nudge carries no serverDateUpdated (null),
            // meaning a consumer simply always re-fetches on a comment nudge (#294).
            _changeMarkers.Record(taskId, serverDateUpdatedMs: null, CommentFields);
            // ClickUp returns the new comment's id as a JSON number on create but as a string on the
            // GET read path; stringify so callers see the same id from both (and re-fetch matches).
            return new CommentItem(
                Id: created?.Id?.ToString(CultureInfo.InvariantCulture) ?? "",
                Author: "",
                DateMs: created?.Date,
                Text: text,
                Resolved: false,
                TaskId: taskId);
        });
    }

    /// <summary>
    /// Post a comment carrying <b>structured runs</b> — literal text and/or @-mention tag blocks (#322) —
    /// to a task (<c>POST /task/{id}/comment</c>); the write substrate for the #325 @-mention composer.
    /// The plain-text <see cref="CreateTaskCommentAsync(string, string, CancellationToken)"/> overload is
    /// unchanged and stays the path for mention-free comments; this one is used when a comment tags members.
    /// <para>
    /// The <paramref name="runs"/> are mapped to ClickUp's structured <c>comment</c> blocks array
    /// (<see cref="CommentRun.Text"/> → <c>{ text }</c>, <see cref="CommentRun.Mention"/> →
    /// <c>{ type:"tag", user:{ id } }</c>) and posted <b>without</b> <c>comment_text</c> — ClickUp fills the
    /// rendered <c>@Name</c> text server-side (G spike, #321). <c>notify_all</c> is sent as <c>false</c>.
    /// </para>
    /// <para>
    /// Same minimal-response contract as the plain path: ClickUp's create response echoes only the new
    /// comment's <c>id</c>/<c>date</c> (not the text/author/blocks), so the returned <see cref="CommentItem"/>
    /// is built from those plus a flattened text preview of the runs — a <see cref="CommentRun.Mention"/>
    /// renders as <c>@{id}</c> until the next fetch reconciles the real name — with the tagged ids surfaced on
    /// <see cref="CommentItem.MentionedUserIds"/> and <see cref="CommentItem.Author"/> left empty for the
    /// caller's optimistic row to stamp. The same id-stringify quirk applies (create returns the id as a JSON
    /// number, the GET read path as a string).
    /// </para>
    /// </summary>
    public Task<CommentItem> CreateTaskCommentAsync(string taskId, IReadOnlyList<CommentRun> runs, CancellationToken ct = default)
    {
        // Build + guard synchronously (before Guard's async body) so an empty body throws at the boundary
        // without a network call — mirrors the plain path's ThrowIfNullOrWhiteSpace.
        var blocks = BuildCommentBlocks(runs);
        return Guard("CreateTaskComment", async () =>
        {
            var request = new CreateCommentRequest { Comment = blocks, NotifyAll = false };
            var created = await _client.V2.Task[taskId].Comment.PostAsync(request, cancellationToken: ct);
            // Same nudge as the plain path: a comment bumps the task's date_updated, but the create response
            // returns only the comment's own id/date, so the marker carries no serverDateUpdated (#294).
            _changeMarkers.Record(taskId, serverDateUpdatedMs: null, CommentFields);
            return BuildStructuredCommentItem(created, runs, taskId);
        });
    }

    /// <summary>
    /// Maps domain <see cref="CommentRun"/>s to generated <see cref="CommentBlock"/>s, guarding an empty
    /// body at the boundary. Since the spec dropped <c>comment_text</c> from <c>required</c> once blocks
    /// exist (#322), ClickUp would otherwise accept and 400 an empty comment — so reject a null/empty run
    /// list and a list carrying no mention whose text runs are all blank (mirrors the plain path's
    /// <c>ThrowIfNullOrWhiteSpace</c>). Text runs are preserved verbatim (leading/trailing spacing between
    /// mentions is meaningful); a mention run serializes to exactly <c>{ type:"tag", user:{ id } }</c>
    /// because Kiota omits the unset <c>text</c>/<c>username</c>/<c>email</c> members.
    /// </summary>
    private static List<CommentBlock> BuildCommentBlocks(IReadOnlyList<CommentRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0)
            throw new ArgumentException("A comment must have at least one run.", nameof(runs));

        var hasText = false;
        var hasMention = false;
        var blocks = new List<CommentBlock>(runs.Count);
        foreach (var run in runs)
        {
            switch (run)
            {
                case CommentRun.Text t:
                    if (!string.IsNullOrWhiteSpace(t.Value)) hasText = true;
                    // Coalesce a null Value (the record's type is non-nullable but nothing enforces it at
                    // runtime) so a stray `{}` block never reaches ClickUp; a blank text run stays "".
                    blocks.Add(new CommentBlock { Text = t.Value ?? "" });
                    break;
                case CommentRun.Mention m:
                    hasMention = true;
                    blocks.Add(new CommentBlock { Type = "tag", User = new User { Id = m.UserId } });
                    break;
                default:
                    throw new ArgumentException($"Unsupported comment run '{run.GetType().Name}'.", nameof(runs));
            }
        }

        if (!hasText && !hasMention)
            throw new ArgumentException("A comment must carry at least one non-empty text run or a mention.", nameof(runs));
        return blocks;
    }

    /// <summary>
    /// Builds the optimistic <see cref="CommentItem"/> for a structured post from the minimal create
    /// response plus the posted runs: a flattened text preview (mentions rendered as <c>@{id}</c>) and the
    /// tagged ids on <see cref="CommentItem.MentionedUserIds"/>. Shares the create-response id-stringify /
    /// empty-author contract with the plain path.
    /// </summary>
    private static CommentItem BuildStructuredCommentItem(CreateCommentResponse? created, IReadOnlyList<CommentRun> runs, string taskId)
    {
        var preview = new System.Text.StringBuilder();
        var mentionedIds = new List<long>();
        foreach (var run in runs)
        {
            switch (run)
            {
                case CommentRun.Text t:
                    preview.Append(t.Value);
                    break;
                case CommentRun.Mention m:
                    preview.Append('@').Append(m.UserId.ToString(CultureInfo.InvariantCulture));
                    mentionedIds.Add(m.UserId);
                    break;
            }
        }

        return new CommentItem(
            Id: created?.Id?.ToString(CultureInfo.InvariantCulture) ?? "",
            Author: "",
            DateMs: created?.Date,
            Text: preview.ToString(),
            Resolved: false,
            TaskId: taskId)
        {
            MentionedUserIds = mentionedIds,
        };
    }

    /// <summary>
    /// The replies in a comment's thread (<c>GET /comment/{comment_id}/reply</c>, #327), mapped to the
    /// stable <see cref="CommentItem"/> shape. Unlike the flat task-comment endpoint this one is
    /// <b>not cursor-paginated</b> — ClickUp returns a thread's replies in a single response (a thread is
    /// bounded by its parent comment) — so this does one fetch and maps via <see cref="MapComment"/>.
    /// A reply payload carries no task context, so <see cref="CommentItem.TaskId"/> is left null for the
    /// caller (the thread loader, #328) to stamp from the parent comment. Empty when the comment has no
    /// replies.
    /// </summary>
    public Task<IReadOnlyList<CommentItem>> GetThreadedCommentsAsync(string commentId, CancellationToken ct = default)
        => Guard("GetThreadedComments", async () =>
        {
            var resp = await _client.V2.Comment[commentId].Reply.GetAsync(cancellationToken: ct);
            return (IReadOnlyList<CommentItem>)(resp?.Comments?
                .Select(c => MapComment(c, null))
                .ToList() ?? []);
        });

    /// <summary>
    /// Post a <b>plain-text</b> reply into a comment's thread (<c>POST /comment/{comment_id}/reply</c>,
    /// #327) and return it as a <see cref="CommentItem"/> for optimistic append. Mirrors
    /// <see cref="CreateTaskCommentAsync"/>: only <c>comment_text</c> is sent (rich content — @-mentions,
    /// task links — is a later epic) with <c>notify_all=false</c>, and because ClickUp's create response
    /// is minimal (<c>id</c>/<c>hist_id</c>/<c>date</c> only, no text/author/blocks) the returned item
    /// echoes the posted <paramref name="text"/> and leaves <see cref="CommentItem.Author"/> empty for the
    /// caller's optimistic row to stamp. <see cref="CommentItem.TaskId"/> is null — the reply endpoint is
    /// keyed by comment, not task.
    /// </summary>
    public Task<CommentItem> CreateThreadedCommentAsync(string commentId, string text, CancellationToken ct = default)
    {
        // ClickUp rejects an empty comment_text with a 400; fail faster and clearer at the boundary.
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return Guard("CreateThreadedComment", async () =>
        {
            var request = new CreateCommentRequest { CommentText = text, NotifyAll = false };
            var created = await _client.V2.Comment[commentId].Reply.PostAsync(request, cancellationToken: ct);
            // Same id quirk as CreateTaskCommentAsync: ClickUp returns the new comment's id as a JSON
            // number on create but as a string on the GET read path; stringify so both paths agree.
            return new CommentItem(
                Id: created?.Id?.ToString(CultureInfo.InvariantCulture) ?? "",
                Author: "",
                DateMs: created?.Date,
                Text: text,
                Resolved: false,
                TaskId: null);
        });
    }

    // ── Change-marker nudges (#294) ─────────────────────────────────────────

    // Advisory field-name hints stamped on a marker (the consumer re-fetches everything, so these are
    // diagnostics only). Static readonly so each write path shares one allocation.
    private static readonly string[] StatusFields = ["status"];
    private static readonly string[] PriorityFields = ["priority"];
    private static readonly string[] DescriptionFields = ["description"];
    private static readonly string[] AssigneeFields = ["assignees"];
    private static readonly string[] CommentFields = ["comment"];
    private static readonly string[] ListsFields = ["lists"];

    /// <summary>
    /// Records a change-marker nudge for a confirmed <c>PUT /task</c> write (#294), carrying the
    /// server-confirmed <c>date_updated</c> parsed off the response so a consumer holding the task can
    /// suppress a redundant fetch. Called only after a 2xx (a non-2xx throws before reaching it); the
    /// store swallows its own failures, so this never affects the write's result.
    /// </summary>
    private void Nudge(string taskId, TaskObject? updated, string[] changedFields)
        => _changeMarkers.Record(taskId, ParseMs(updated?.DateUpdated), changedFields);

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
        MentionedUserIds: MapMentionedUserIds(c.CommentProp),
        ReplyCount: ParseCount(c.ReplyCount));

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
        // The API may omit `checklists` entirely (older/edge responses) → empty list, never a null-deref.
        Checklists = t.Checklists?.Select(MapChecklist).ToList() ?? [],
    };

    /// <summary>
    /// Maps a generated <see cref="Checklist"/> onto the stable <see cref="TaskChecklist"/> (#454). The
    /// container fields are read from the typed generated properties; the loosely-typed <c>items</c> array
    /// rides on Kiota's <c>AdditionalData</c>, so — mirroring <see cref="MapCustomField"/> — the checklist
    /// is re-serialized to JSON and its items read back via the pure <see cref="ChecklistReader"/>, so no
    /// generated type escapes this facade and every item-shape tolerance lives in one testable place.
    /// internal (not private) so the mapping can be unit-tested without hitting the live API.
    /// </summary>
    internal static TaskChecklist MapChecklist(Checklist c)
    {
        IReadOnlyList<TaskChecklistItem> items;
        try
        {
            items = ChecklistReader.ReadItems(SerializeToJson(c));
        }
        catch
        {
            // One malformed checklist must never sink the whole task's detail — degrade to an empty item
            // list (the counts/name still render), mirroring MapCustomField's defensive fallback.
            items = [];
        }

        return new TaskChecklist(
            Id: c.Id ?? "",
            Name: c.Name ?? "",
            OrderIndex: c.Orderindex,
            Resolved: c.Resolved ?? 0,
            Unresolved: c.Unresolved ?? 0,
            Items: items);
    }

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
            return new CustomFieldItem(f.Name!, f.Type, value, options, f.Id);
        }
        catch
        {
            // One malformed/unexpected field must never sink the whole task's detail — degrade to
            // name/type/id only (the same shape the tab showed before values were surfaced; the id is
            // kept so strand detection, #365, still identifies the field).
            return new CustomFieldItem(f.Name!, f.Type, Id: f.Id);
        }
    }

    /// <summary>
    /// Maps a generated field <b>definition</b> onto the stable <see cref="CustomFieldDefinition"/>,
    /// including its drop-down/label <c>type_config.options</c>. The generated type surfaces
    /// <c>id/name/type/required</c> as typed properties; the loosely-typed <c>type_config</c> lands in
    /// Kiota's <c>AdditionalData</c>, so we re-serialize the definition to JSON (the same faithful
    /// round-trip <see cref="MapCustomField"/> uses) and read the options back with
    /// <see cref="System.Text.Json"/> — no generated type escapes the facade (#249).
    /// </summary>
    internal static CustomFieldDefinition MapCustomFieldDefinition(GenCustomFieldDefinition f)
    {
        try
        {
            var options = CustomFieldReader.ReadOptions(SerializeToJson(f));
            return new CustomFieldDefinition(f.Id ?? "", f.Name ?? "", f.Type, f.Required ?? false, options);
        }
        catch
        {
            // One malformed field must never sink the whole list's field fetch — degrade to
            // identity + required only (no options), mirroring MapCustomField's defensive fallback.
            return new CustomFieldDefinition(f.Id ?? "", f.Name ?? "", f.Type, f.Required ?? false);
        }
    }

    /// <summary>
    /// Converts a neutral <see cref="JsonElement"/> (the domain-side shape of a custom-field value, #368)
    /// into the Kiota <see cref="UntypedNode"/> the JSON serializer writes to the request body. Lives at
    /// the facade boundary so the loosely-typed value can cross into the generated client without a domain
    /// type ever referencing a Kiota type. Recurses arrays/objects; integral numbers become
    /// <see cref="UntypedLong"/> so an epoch-ms date/option-id-count doesn't gain a spurious decimal.
    /// </summary>
    internal static UntypedNode ToUntyped(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => new UntypedString(value.GetString()),
        JsonValueKind.True or JsonValueKind.False => new UntypedBoolean(value.GetBoolean()),
        JsonValueKind.Number => value.TryGetInt64(out var l)
            ? new UntypedLong(l)
            : new UntypedDouble(value.GetDouble()),
        JsonValueKind.Array => new UntypedArray(value.EnumerateArray().Select(ToUntyped)),
        JsonValueKind.Object => new UntypedObject(value.EnumerateObject()
            .ToDictionary(p => p.Name, p => ToUntyped(p.Value))),
        // Null / Undefined and any unexpected kind serialize as an explicit JSON null.
        _ => new UntypedNull(),
    };

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

    /// <summary>Parses ClickUp's string-typed <c>reply_count</c> to a non-negative int; a missing,
    /// unparseable, or negative value yields 0 (#327).</summary>
    private static int ParseCount(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 0;

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

    /// <summary>
    /// Non-generic <see cref="Guard{T}"/> for writes whose response carries nothing useful (an empty
    /// <c>{}</c> body): still translates Kiota <see cref="ApiException"/> into <see cref="ClickUpApiException"/>.
    /// </summary>
    private static async Task Guard(string operation, Func<Task> call)
    {
        try
        {
            await call();
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
