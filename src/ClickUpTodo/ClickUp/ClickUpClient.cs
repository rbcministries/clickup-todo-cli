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
public sealed class ClickUpClient : IDisposable
{
    private const int PageSize = 100; // ClickUp returns at most 100 tasks per page.

    private readonly HttpClientRequestAdapter _adapter;
    private readonly ClickUpApiClient _client;

    /// <summary>Drives the client with any Kiota auth provider (personal token or OAuth).</summary>
    public ClickUpClient(IAuthenticationProvider authProvider, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(authProvider);
        _adapter = new HttpClientRequestAdapter(authProvider, httpClient: httpClient);
        _client = new ClickUpApiClient(_adapter);
    }

    /// <summary>Drives the client with a ClickUp personal API token (sent as a raw header).</summary>
    public ClickUpClient(string token, HttpClient? httpClient = null)
        : this(new ClickUpTokenAuthProvider(token), httpClient)
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
    /// </summary>
    public Task<List<TaskItem>> GetAssignedTasksAsync(string workspaceId, IReadOnlyList<long> assigneeIds, CancellationToken ct = default)
        => Guard("GetFilteredTeamTasks", () => PageAsync(page =>
            _client.V2.Team[workspaceId].Task.GetAsync(cfg =>
            {
                if (assigneeIds.Count > 0)
                    cfg.QueryParameters.Assignees = assigneeIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray();
                cfg.QueryParameters.Page = page;
                cfg.QueryParameters.IncludeClosed = false;
                cfg.QueryParameters.Subtasks = true;
            }, ct), ct));

    /// <summary>All open tasks on a specific list, de-paged.</summary>
    public Task<List<TaskItem>> GetListTasksAsync(string listId, CancellationToken ct = default)
        => Guard("GetTasks", () => PageAsync(page =>
            _client.V2.List[listId].Task.GetAsync(cfg =>
            {
                cfg.QueryParameters.Page = page;
                cfg.QueryParameters.IncludeClosed = false;
                cfg.QueryParameters.Subtasks = true;
                cfg.QueryParameters.Archived = false;
            }, ct), ct));

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
                onCapReached: null,
                ct);
            return (IReadOnlyList<CommentItem>)comments.Select(c => MapComment(c, taskId)).ToList();
        });

    // ── Mapping & plumbing ──────────────────────────────────────────────────

    // internal (not private) so the mapping can be unit-tested without hitting the live API.
    internal static TaskItem Map(TaskObject t)
    {
        var priorityLevel = ClickUpPriority.Level(t.Priority?.Id, t.Priority?.PriorityProp);
        return new()
        {
            Id = t.Id ?? "",
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
    /// <see cref="CommentItem.DateMs"/>. internal (not private) so it can be unit-tested offline.
    /// </summary>
    internal static CommentItem MapComment(Comment c, string? taskId) => new(
        Id: c.Id ?? "",
        Author: DisplayName(c.User),
        DateMs: ParseMs(c.Date),
        Text: c.CommentText ?? "",
        Resolved: c.Resolved == true,
        TaskId: taskId);

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

            var freshCount = 0;
            foreach (var c in page)
            {
                // A blank id can't be de-duped (and can't anchor a cursor) — keep it either way.
                if (string.IsNullOrEmpty(c.Id) || seenIds.Add(c.Id!))
                {
                    all.Add(c);
                    freshCount++;
                }
            }

            // Last page (short/empty), or a full page that added nothing new (cursor stuck) ⇒ done.
            if (page.Count < CommentPageSize || freshCount == 0)
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

    public void Dispose() => _adapter.Dispose();
}
