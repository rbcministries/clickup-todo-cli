using System.Net;
using System.Text;
using System.Text.Json;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Offline tests for the threaded-comment (reply) facade methods on <see cref="ClickUpClient"/> (#327).
/// They drive the real generated client through a capturing <see cref="HttpMessageHandler"/> (no token,
/// no network), asserting the outgoing <c>GET</c>/<c>POST /comment/{comment_id}/reply</c> shape and the
/// mapping back to <see cref="CommentItem"/>. The reply endpoint is keyed by comment (not task) and its
/// create response is minimal — the same contract as <see cref="ClickUpClient.CreateTaskCommentAsync"/>.
/// </summary>
public sealed class ClickUpClientThreadedCommentTests
{
    [Fact]
    public async Task GetThreadedComments_FetchesReplyEndpoint_AndMapsReplies()
    {
        var handler = new CapturingHandler("""
            {
              "comments": [
                { "id": "r1", "comment_text": "first reply", "user": { "username": "Ann" }, "date": "1699000000001", "resolved": false, "reply_count": "0" },
                { "id": "r2", "comment_text": "second", "user": { "id": 5 }, "date": "1699000000002" }
              ]
            }
            """);
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var replies = await client.GetThreadedCommentsAsync("c100");

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Contains("/v2/comment/c100/reply", handler.RequestUri);

        Assert.Equal(2, replies.Count);
        Assert.Equal("r1", replies[0].Id);
        Assert.Equal("Ann", replies[0].Author);
        Assert.Equal("first reply", replies[0].Text);
        Assert.Equal(1_699_000_000_001, replies[0].DateMs);
        // A reply payload carries no task context — TaskId stays null for the loader (#328) to stamp.
        Assert.Null(replies[0].TaskId);
        Assert.Equal("5", replies[1].Author); // id fallback
    }

    [Fact]
    public async Task GetThreadedComments_NoReplies_YieldsEmptyList()
    {
        var handler = new CapturingHandler("""{ "comments": [] }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var replies = await client.GetThreadedCommentsAsync("c1");

        Assert.Empty(replies);
    }

    [Fact]
    public async Task GetThreadedComments_MissingCommentsArray_YieldsEmptyList()
    {
        // A sparser-than-expected body must degrade to an empty thread, never throw.
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        Assert.Empty(await client.GetThreadedCommentsAsync("c1"));
    }

    [Fact]
    public async Task CreateThreadedComment_PostsToReplyEndpoint_AndReturnsCreatedComment()
    {
        // Response deliberately omits comment_text/user: proves the returned Text is the posted text
        // echoed back (not read off the response) and Author is left empty for the caller. The id is a
        // JSON *number* here — the reply create endpoint shares CreateTaskCommentAsync's contract, where
        // ClickUp returns the id as int64 on create (string on the read path); the facade stringifies it.
        var handler = new CapturingHandler("""{ "id": 90140228459981, "hist_id": "h456", "date": 1568036964079 }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var created = await client.CreateThreadedCommentAsync("c100", "On it 🚀");

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Contains("/v2/comment/c100/reply", handler.RequestUri);

        var body = handler.Body!.RootElement;
        Assert.Equal("On it 🚀", body.GetProperty("comment_text").GetString());
        Assert.Equal(JsonValueKind.False, body.GetProperty("notify_all").ValueKind);
        // Rich content (structured `comment` blocks) is out of scope for a plain-text reply.
        Assert.False(body.TryGetProperty("comment", out _), "a plain-text reply must not send rich `comment` blocks.");

        Assert.Equal("90140228459981", created.Id);
        Assert.Equal(1568036964079L, created.DateMs);
        Assert.Equal("On it 🚀", created.Text);
        Assert.Equal("", created.Author);
        Assert.False(created.Resolved);
        // The reply endpoint is keyed by comment, not task, so there is no task attribution.
        Assert.Null(created.TaskId);
    }

    [Fact]
    public async Task CreateThreadedComment_MinimalResponse_StillEchoesText_AndDoesNotThrow()
    {
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var created = await client.CreateThreadedCommentAsync("c9", "just a note");

        Assert.Equal("", created.Id);
        Assert.Null(created.DateMs);
        Assert.Equal("just a note", created.Text);
        Assert.Null(created.TaskId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateThreadedComment_RejectsEmptyText_WithoutHittingTheNetwork(string? text)
    {
        // Empty comment_text is a 400 at ClickUp; the facade guards it at the boundary so the request
        // is never sent (the handler would record a Method if reached, proving the guard fires first).
        var handler = new CapturingHandler("""{ "id": "r1" }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        // null → ArgumentNullException, empty/whitespace → ArgumentException; both derive from
        // ArgumentException, so ThrowsAny accepts either.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.CreateThreadedCommentAsync("c1", text!));

        Assert.Null(handler.Method);
    }

    /// <summary>Records the outgoing request (method, URI, parsed JSON body) and returns a canned body.</summary>
    private sealed class CapturingHandler(string responseBody) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? RequestUri { get; private set; }
        public JsonDocument? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri?.ToString();
            if (request.Content is not null)
                Body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
