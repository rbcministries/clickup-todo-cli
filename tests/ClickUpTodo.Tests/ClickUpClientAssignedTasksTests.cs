using System.Net;
using System.Text;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Offline request-building tests for <see cref="ClickUpClient.GetAssignedTasksAsync"/>'s optional
/// look-back window (#244). They drive the real generated client through a capturing
/// <see cref="HttpMessageHandler"/> (no token, no network) and assert that the <c>date_updated_gt</c>
/// query parameter is on the outgoing request only when a window is supplied — so the default (null)
/// path is byte-for-byte the pre-#244 request. The empty-task response terminates de-paging after the
/// first page.
/// </summary>
public sealed class ClickUpClientAssignedTasksTests
{
    [Fact]
    public async Task GetAssignedTasks_NoWindow_OmitsDateUpdatedGt()
    {
        var handler = new CapturingHandler("""{ "tasks": [] }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.GetAssignedTasksAsync("ws1", [7L]);

        Assert.DoesNotContain("date_updated_gt", handler.RequestUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAssignedTasks_WithWindow_SendsDateUpdatedGt()
    {
        var handler = new CapturingHandler("""{ "tasks": [] }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.GetAssignedTasksAsync("ws1", [7L], updatedAfterMs: 1_700_000_000_000);

        Assert.Contains("date_updated_gt=1700000000000", handler.RequestUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAssignedTasks_WindowComposesWithIncludeClosed()
    {
        // The window narrows by time; include_closed governs closed tasks — orthogonal, both ride along.
        var handler = new CapturingHandler("""{ "tasks": [] }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.GetAssignedTasksAsync("ws1", [7L], includeClosed: true, updatedAfterMs: 1_700_000_000_000);

        Assert.Contains("date_updated_gt=1700000000000", handler.RequestUri, StringComparison.Ordinal);
        Assert.Contains("include_closed=true", handler.RequestUri, StringComparison.Ordinal);
    }

    /// <summary>Captures the outgoing request URI and returns a canned (empty) task page.</summary>
    private sealed class CapturingHandler(string body) : HttpMessageHandler
    {
        public string RequestUri { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString() ?? "";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
