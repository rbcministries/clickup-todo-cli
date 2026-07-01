using System.Globalization;
using System.Text.Json;
using ClickUpTodo.ClickUp.Generated;
using ClickUpTodo.ClickUp.Generated.Models;
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

    public ClickUpClient(string token, HttpClient? httpClient = null)
    {
        _adapter = new HttpClientRequestAdapter(new ClickUpTokenAuthProvider(token), httpClient: httpClient);
        _client = new ClickUpApiClient(_adapter);
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

    /// <summary>All open tasks across the workspace assigned to <paramref name="userId"/>, de-paged.</summary>
    public Task<List<TaskItem>> GetAssignedTasksAsync(string workspaceId, long userId, CancellationToken ct = default)
        => Guard("GetFilteredTeamTasks", () => PageAsync(page =>
            _client.V2.Team[workspaceId].Task.GetAsync(cfg =>
            {
                cfg.QueryParameters.Assignees = [userId.ToString(CultureInfo.InvariantCulture)];
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

    /// <summary>The comments on a task, mapped to the stable <see cref="CommentItem"/> shape.</summary>
    public Task<IReadOnlyList<CommentItem>> GetTaskCommentsAsync(string taskId, CancellationToken ct = default)
        => Guard("GetTaskComments", async () =>
        {
            var comments = (await _client.V2.Task[taskId].Comment.GetAsync(cancellationToken: ct))?.Comments ?? [];
            return (IReadOnlyList<CommentItem>)comments.Select(c => new CommentItem(
                Id: c.Id ?? "",
                Author: DisplayName(c.User),
                DateMs: ParseMs(c.Date),
                Text: c.CommentText ?? "",
                Resolved: c.Resolved == true)).ToList();
        });

    // ── Mapping & plumbing ──────────────────────────────────────────────────

    private static TaskItem Map(TaskObject t) => new()
    {
        Id = t.Id ?? "",
        Name = t.Name ?? "(untitled)",
        Url = t.Url,
        ParentId = string.IsNullOrWhiteSpace(t.Parent) ? null : t.Parent,
        DueDateMs = ParseMs(t.DueDate),
        UpdatedMs = ParseMs(t.DateUpdated),
        ListId = t.List?.Id,
        ListName = t.List?.Name,
        StatusName = t.Status?.StatusProp,
        StatusColor = t.Status?.Color,
    };

    private static TaskDetail MapDetail(TaskObject t) => new()
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
