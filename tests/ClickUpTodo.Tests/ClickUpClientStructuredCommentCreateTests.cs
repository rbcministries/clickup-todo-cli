using System.Net;
using System.Text;
using System.Text.Json;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tests;

/// <summary>
/// Offline tests for the <b>structured</b> create-comment write path on the <see cref="ClickUpClient"/>
/// facade (#322) — the overload that takes <see cref="CommentRun"/>s so a comment can carry @-mention tag
/// blocks. They drive the real generated client through a capturing <see cref="HttpMessageHandler"/> (no
/// token, no network), asserting the outgoing <c>POST /task/{id}/comment</c> body is the structured
/// <c>comment</c> blocks array (never <c>comment_text</c>) and that the facade builds the returned
/// <see cref="CommentItem"/> from the minimal create response plus a preview of the runs it posted.
/// </summary>
public sealed class ClickUpClientStructuredCommentCreateTests
{
    [Fact]
    public async Task CreateTaskComment_WithRuns_PostsStructuredBlocks_AndNoCommentText()
    {
        // Canned create response is minimal (id as a JSON number + date), like the real endpoint: proving
        // the returned Text is the posted preview, not read back, and the id is stringified.
        var handler = new CapturingHandler("""{ "id": 90140228459974, "date": 1568036964079 }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var created = await client.CreateTaskCommentAsync(
            "t1",
            [new CommentRun.Text("hi "), new CommentRun.Mention(183)]);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Contains("/v2/task/t1/comment", handler.RequestUri);

        var body = handler.Body!.RootElement;
        // A structured post sends the `comment` blocks array and NOT `comment_text` (they are mutually
        // exclusive — ClickUp fills the rendered @Name server-side).
        Assert.False(body.TryGetProperty("comment_text", out _), "a structured comment must not send comment_text.");
        Assert.Equal(JsonValueKind.False, body.GetProperty("notify_all").ValueKind);

        var blocks = body.GetProperty("comment");
        Assert.Equal(JsonValueKind.Array, blocks.ValueKind);
        Assert.Equal(2, blocks.GetArrayLength());

        // [0] plain text run → { "text": "hi " } (spacing preserved verbatim).
        var textBlock = blocks[0];
        Assert.Equal("hi ", textBlock.GetProperty("text").GetString());
        Assert.False(textBlock.TryGetProperty("type", out _));
        Assert.False(textBlock.TryGetProperty("user", out _));

        // [1] mention run → { "type": "tag", "user": { "id": 183 } } — no stray text/username/email.
        var tagBlock = blocks[1];
        Assert.Equal("tag", tagBlock.GetProperty("type").GetString());
        Assert.Equal(183L, tagBlock.GetProperty("user").GetProperty("id").GetInt64());
        Assert.False(tagBlock.TryGetProperty("text", out _), "a mention tag block must not carry text.");
        var user = tagBlock.GetProperty("user");
        Assert.False(user.TryGetProperty("username", out _));
        Assert.False(user.TryGetProperty("email", out _));

        // The returned optimistic item: stringified id, echoed date, a flattened preview (mention → @{id}),
        // the tagged id surfaced, TaskId stamped, Author left empty for the caller's row.
        Assert.Equal("90140228459974", created.Id);
        Assert.Equal(1568036964079L, created.DateMs);
        Assert.Equal("hi @183", created.Text);
        Assert.Equal(new long[] { 183 }, created.MentionedUserIds);
        Assert.Equal("t1", created.TaskId);
        Assert.Equal("", created.Author);
        Assert.False(created.Resolved);
    }

    [Fact]
    public async Task CreateTaskComment_SingleMention_SerializesExactlyOneTagBlock()
    {
        var handler = new CapturingHandler("""{ "id": 1, "date": 1 }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var created = await client.CreateTaskCommentAsync("t1", [new CommentRun.Mention(42)]);

        var blocks = handler.Body!.RootElement.GetProperty("comment");
        Assert.Equal(1, blocks.GetArrayLength());
        Assert.Equal("tag", blocks[0].GetProperty("type").GetString());
        Assert.Equal(42L, blocks[0].GetProperty("user").GetProperty("id").GetInt64());
        Assert.False(blocks[0].TryGetProperty("text", out _));

        Assert.Equal("@42", created.Text);
        Assert.Equal(new long[] { 42 }, created.MentionedUserIds);
    }

    [Fact]
    public async Task CreateTaskComment_MinimalResponse_StillBuildsPreview_AndDoesNotThrow()
    {
        // A sparser body (no id/date) must map to empty/null rather than throwing; the preview is still
        // built from the posted runs so the UI can render the optimistic row.
        var handler = new CapturingHandler("{}");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var created = await client.CreateTaskCommentAsync(
            "t9",
            [new CommentRun.Text("pinging "), new CommentRun.Mention(7), new CommentRun.Text(" and "), new CommentRun.Mention(8)]);

        Assert.Equal("", created.Id);
        Assert.Null(created.DateMs);
        Assert.Equal("pinging @7 and @8", created.Text);
        Assert.Equal(new long[] { 7, 8 }, created.MentionedUserIds);
        Assert.Equal("t9", created.TaskId);
    }

    [Fact]
    public async Task CreateTaskComment_NullRuns_Throws_WithoutHittingTheNetwork()
    {
        var handler = new CapturingHandler("""{ "id": 1 }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.CreateTaskCommentAsync("t1", (IReadOnlyList<CommentRun>)null!));

        Assert.Null(handler.Method);
    }

    [Fact]
    public async Task CreateTaskComment_EmptyRunList_Throws_WithoutHittingTheNetwork()
    {
        var handler = new CapturingHandler("""{ "id": 1 }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateTaskCommentAsync("t1", []));

        Assert.Null(handler.Method);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateTaskComment_TextOnlyBlankRuns_Throw_WithoutHittingTheNetwork(string blank)
    {
        // With comment_text dropped from `required`, an all-blank body would otherwise reach ClickUp and
        // 400 — so the facade rejects a run list with no mention whose text runs are all blank.
        var handler = new CapturingHandler("""{ "id": 1 }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateTaskCommentAsync("t1", [new CommentRun.Text(blank)]));

        Assert.Null(handler.Method);
    }

    [Fact]
    public async Task CreateTaskComment_BlankTextButHasMention_IsAllowed()
    {
        // A blank text run is meaningless alone but fine alongside a mention (the mention satisfies the
        // non-empty-body guard); the blank block is preserved verbatim.
        var handler = new CapturingHandler("""{ "id": 5, "date": 5 }""");
        using var client = new ClickUpClient("pk_x", new HttpClient(handler));

        var created = await client.CreateTaskCommentAsync("t1", [new CommentRun.Text(" "), new CommentRun.Mention(9)]);

        Assert.Equal(HttpMethod.Post, handler.Method);
        var blocks = handler.Body!.RootElement.GetProperty("comment");
        Assert.Equal(2, blocks.GetArrayLength());
        Assert.Equal(" ", blocks[0].GetProperty("text").GetString());
        Assert.Equal(new long[] { 9 }, created.MentionedUserIds);
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
