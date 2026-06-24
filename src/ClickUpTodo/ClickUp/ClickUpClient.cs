using System.Globalization;
using ClickUpTodo.ClickUp.Generated;
using ClickUpTodo.ClickUp.Generated.Models;
using Microsoft.Kiota.Http.HttpClientLibrary;
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

    /// <summary>Set a task's status. <paramref name="statusName"/> must be one of its list's statuses.</summary>
    public Task SetTaskStatusAsync(string taskId, string statusName, CancellationToken ct = default)
        => Guard("UpdateTask", async () =>
        {
            await _client.V2.Task[taskId].PutAsync(new UpdateTaskRequest { Status = statusName }, cancellationToken: ct);
            return true;
        });

    // ── Mapping & plumbing ──────────────────────────────────────────────────

    private static TaskItem Map(TaskObject t) => new()
    {
        Id = t.Id ?? "",
        Name = t.Name ?? "(untitled)",
        Url = t.Url,
        DueDateMs = long.TryParse(t.DueDate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? ms : null,
        ListId = t.List?.Id,
        ListName = t.List?.Name,
        StatusName = t.Status?.StatusProp,
        StatusColor = t.Status?.Color,
    };

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
