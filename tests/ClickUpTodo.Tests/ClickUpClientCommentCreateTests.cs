using System.Net;
using System.Text;
using System.Text.Json;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Offline tests for the create-comment write method on the <see cref="ClickUpClient"/> facade (#210).
/// They drive the real generated client through a capturing <see cref="HttpMessageHandler"/> (no token,
/// no network), asserting the outgoing <c>POST /task/{id}/comment</c> body shape and that the facade
/// builds the returned <see cref="CommentItem"/> from the (minimal) create response plus the plain text
/// it just posted — ClickUp's create response omits the text/author/blocks, so the text must be echoed,
/// not read back.
/// </summary>
public sealed class ClickUpClientCommentCreateTests
{
    [Fact]
    public async Task CreateTaskComment_PostsCommentTextBody_AndReturnsCreatedComment()
    {
        // The canned response deliberately omits comment_text/user: proving the returned Text is the
        // posted text echoed back (not read off the response) and Author is left empty for the caller.
        // The id is a JSON *number* here — that's what ClickUp's create-comment endpoint really returns
        // (the GET read path returns it as a string), and the facade stringifies it so both paths agree.
        // A regression where the response id was typed as a string dropped it to null/empty (#144 run).
        var handler = new CapturingHandler("""{ "id": 90140228459974, "hist_id": "h456", "date": 1568036964079 }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var created = await client.CreateTaskCommentAsync("t1", "Ship it 🚀");

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Contains("/v2/task/t1/comment", handler.RequestUri);

        var body = handler.Body!.RootElement;
        Assert.Equal(JsonValueKind.String, body.GetProperty("comment_text").ValueKind);
        Assert.Equal("Ship it 🚀", body.GetProperty("comment_text").GetString());

        Assert.Equal("90140228459974", created.Id);
        Assert.Equal(1568036964079L, created.DateMs);
        Assert.Equal("Ship it 🚀", created.Text);
        Assert.Equal("t1", created.TaskId);
        Assert.Equal("", created.Author);
        Assert.False(created.Resolved);
    }

    [Fact]
    public async Task CreateTaskComment_SendsNotifyAllFalse_AndNoRichBlocks()
    {
        var handler = new CapturingHandler("""{ "id": 1, "date": 1 }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await client.CreateTaskCommentAsync("t1", "hello");

        var body = handler.Body!.RootElement;
        Assert.Equal(JsonValueKind.False, body.GetProperty("notify_all").ValueKind);
        // Rich content (structured `comment` blocks) is out of scope: a plain-text post must not send it.
        Assert.False(body.TryGetProperty("comment", out _), "a plain-text comment must not send rich `comment` blocks.");
    }

    [Fact]
    public async Task CreateTaskComment_MinimalResponse_StillEchoesText_AndDoesNotThrow()
    {
        // ClickUp occasionally returns a sparser body; a missing id/date must map to empty/null rather
        // than throwing, and the posted text is still echoed so the UI can render the optimistic row.
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var created = await client.CreateTaskCommentAsync("t9", "just a note");

        Assert.Equal("", created.Id);
        Assert.Null(created.DateMs);
        Assert.Equal("just a note", created.Text);
        Assert.Equal("t9", created.TaskId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateTaskComment_RejectsEmptyText_WithoutHittingTheNetwork(string text)
    {
        // Empty comment_text is a 400 at ClickUp; the facade guards it at the boundary so the request
        // is never sent (the handler would throw if reached, proving the guard fires first).
        var handler = new CapturingHandler("""{ "id": 1 }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateTaskCommentAsync("t1", text));

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
