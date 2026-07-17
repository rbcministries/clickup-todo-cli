using System.Net;
using System.Text;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Offline request-building tests for the feed look-back window (#244): drives the real generated
/// client through a capturing <see cref="HttpMessageHandler"/> (no token, no network) and asserts the
/// optional <c>date_updated_gt</c> window on <see cref="ClickUpClient.GetAssignedTasksAsync"/> is
/// emitted only when set, and composes with the independent <c>include_closed</c> flag.
/// </summary>
public sealed class ClickUpClientFeedFetchTests
{
    private const long SampleWindowMs = 1_700_000_000_000;

    [Fact]
    public async Task GetAssignedTasks_NoWindow_OmitsDateUpdatedGt()
    {
        var handler = new CapturingHandler("""{ "tasks": [] }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.GetAssignedTasksAsync("team1", [123]);

        Assert.Contains("/v2/team/team1/task", handler.RequestUri);
        Assert.DoesNotContain("date_updated_gt", handler.RequestUri);
    }

    [Fact]
    public async Task GetAssignedTasks_WithWindow_EmitsDateUpdatedGt()
    {
        var handler = new CapturingHandler("""{ "tasks": [] }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.GetAssignedTasksAsync("team1", [123], updatedAfterMs: SampleWindowMs);

        Assert.Contains("date_updated_gt=1700000000000", handler.RequestUri);
    }

    [Fact]
    public async Task GetAssignedTasks_WindowComposesWithIncludeClosed()
    {
        // date_updated_gt and include_closed are independent query params — the F12 completed toggle
        // must still ride the same GET as the look-back window (the delta path relies on this too).
        var handler = new CapturingHandler("""{ "tasks": [] }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.GetAssignedTasksAsync("team1", [123], includeClosed: true, updatedAfterMs: SampleWindowMs);

        Assert.Contains("date_updated_gt=1700000000000", handler.RequestUri);
        Assert.Contains("include_closed=true", handler.RequestUri);
    }

    private sealed class CapturingHandler(string responseBody) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
