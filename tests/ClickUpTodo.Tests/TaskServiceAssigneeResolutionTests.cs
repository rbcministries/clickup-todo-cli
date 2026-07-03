using System.Net;
using System.Text;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tests;

/// <summary>
/// Tests the async orchestration of <see cref="TaskService.ResolveAssigneeIdsAsync"/> (#73): the
/// members round-trip is skipped on the fast path, taken (and memoized) when a name needs resolving,
/// and degrades best-effort — including on an HttpClient timeout, which surfaces as a
/// <see cref="OperationCanceledException"/> our caller never signalled. A stub
/// <see cref="HttpMessageHandler"/> drives the generated client so the real HTTP/mapping path runs
/// offline (no <c>CLICKUP_TOKEN</c>).
/// </summary>
public sealed class TaskServiceAssigneeResolutionTests
{
    private const string TeamsJson =
        """{ "teams": [ { "id": "ws1", "name": "WS", "members": [ { "user": { "id": 10, "username": "ada", "email": "ada@example.com" } }, { "user": { "id": 20, "username": "bo", "email": "bo@example.com" } } ] } ] }""";

    private sealed class StubHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(Calls));
        }
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (TaskService Svc, StubHandler Handler) Service(Func<int, HttpResponseMessage> respond, long userId = 42)
    {
        var handler = new StubHandler(respond);
        var client = new ClickUpClient("pk_test", new HttpClient(handler));
        var config = new AppConfig { WorkspaceId = "ws1", PersonalTasksListId = "list1" };
        return (new TaskService(client, config, userId), handler);
    }

    private static ViewSettings ViewWith(params FilterRule[] rules) => new() { Filters = [.. rules] };
    private static FilterRule Assignee(string value) => new() { Field = TaskField.Assignee, Op = FilterOp.Is, Value = value };

    [Fact]
    public async Task ResolveAssigneeIdsAsync_NoNames_SkipsMembersFetch()
    {
        var (svc, handler) = Service(_ => Ok(TeamsJson));

        var ids = await svc.ResolveAssigneeIdsAsync(ViewWith(ViewSettings.DefaultAssigneeRule()));

        Assert.Equal([42L], ids);
        Assert.Equal(0, handler.Calls); // the default (me) view never pays the members round-trip
    }

    [Fact]
    public async Task ResolveAssigneeIdsAsync_Name_ResolvesViaMembers_AndMemoizes()
    {
        var (svc, handler) = Service(_ => Ok(TeamsJson));

        var first = await svc.ResolveAssigneeIdsAsync(ViewWith(Assignee("bo")));
        var second = await svc.ResolveAssigneeIdsAsync(ViewWith(Assignee("ada@example.com")));

        Assert.Equal([20L], first);
        Assert.Equal([10L], second);
        Assert.Equal(1, handler.Calls); // members fetched once, reused for the second resolution
    }

    [Fact]
    public async Task ResolveAssigneeIdsAsync_MembersTimeout_FallsBackAndRetriesNextTime()
    {
        // Call 1 simulates an HttpClient request timeout (TaskCanceledException, an
        // OperationCanceledException) that our default (unsignalled) ct never triggered; call 2 succeeds.
        var (svc, handler) = Service(call => call == 1 ? throw new TaskCanceledException("timeout") : Ok(TeamsJson));

        // Best-effort: the timeout does NOT propagate; the name is simply unresolved this time.
        var duringFailure = await svc.ResolveAssigneeIdsAsync(ViewWith(Assignee("ada")));
        Assert.Empty(duringFailure);

        // The failed fetch was not cached, so the next resolution retries and now succeeds.
        var afterRecovery = await svc.ResolveAssigneeIdsAsync(ViewWith(Assignee("ada")));
        Assert.Equal([10L], afterRecovery);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ResolveAssigneeIdsAsync_MembersApiError_FallsBackToMeAndNumeric()
    {
        var (svc, handler) = Service(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom"),
        });

        // "me" and a numeric id still resolve; only the unresolvable name is dropped.
        var ids = await svc.ResolveAssigneeIdsAsync(ViewWith(Assignee("me"), Assignee("99"), Assignee("ada")));

        Assert.Equal([42L, 99L], ids);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ResolveAssigneeIdsAsync_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (svc, _) = Service(_ => Ok(TeamsJson));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.ResolveAssigneeIdsAsync(ViewWith(Assignee("ada")), cts.Token));
    }
}
