using System.Text.Json;
using ClickUpTodo.ClickUp;
using ClickUpTodo.ClickUp.Generated.Models;
using Microsoft.Kiota.Serialization.Json;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for <see cref="ClickUpClient.MapComment"/> — the (offline) mapping from the generated
/// <see cref="Comment"/> onto the stable <see cref="CommentItem"/>. Focused on task attribution
/// (#111), which the feed aggregation (#112) and open-from-feed (#115) rely on, plus the author
/// fallback and date/field degradation.
/// </summary>
public sealed class ClickUpClientCommentMapTests
{
    [Fact]
    public void MapComment_CarriesAllFieldsAndStampsTaskId()
    {
        var c = new Comment
        {
            Id = "c1",
            CommentText = "Looks good.",
            Date = "1699000000000",
            Resolved = true,
            User = new User { Id = 42, Username = "Ben" },
        };

        var mapped = ClickUpClient.MapComment(c, "task-9");

        // Field-by-field rather than whole-record equality: CommentItem now carries a MentionedUserIds
        // collection, which records compare by reference — two equal-content empty collections wouldn't
        // be Equal. This also asserts the no-mention default explicitly.
        Assert.Equal("c1", mapped.Id);
        Assert.Equal("Ben", mapped.Author);
        Assert.Equal(1_699_000_000_000, mapped.DateMs);
        Assert.Equal("Looks good.", mapped.Text);
        Assert.True(mapped.Resolved);
        Assert.Equal("task-9", mapped.TaskId);
        Assert.False(mapped.MentionsMe);
        Assert.Empty(mapped.MentionedUserIds);
        Assert.Equal(0, mapped.ReplyCount); // reply_count absent ⇒ 0
    }

    // ── Reply-count mapping (#327) ─────────────────────────────────────────────

    [Fact]
    public void MapComment_ParsesReplyCountFromString()
        => Assert.Equal(3, ClickUpClient.MapComment(new Comment { Id = "c1", ReplyCount = "3" }, "t").ReplyCount);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("-2")]
    public void MapComment_AbsentOrUnparseableOrNegativeReplyCount_YieldsZero(string? value)
        => Assert.Equal(0, ClickUpClient.MapComment(new Comment { Id = "c1", ReplyCount = value }, "t").ReplyCount);

    [Fact]
    public void MapComment_AuthorFallsBackFromUsernameToEmailToId()
    {
        Assert.Equal("teammate@example.com",
            ClickUpClient.MapComment(new Comment { User = new User { Id = 7, Email = "teammate@example.com" } }, "t").Author);

        Assert.Equal("13",
            ClickUpClient.MapComment(new Comment { User = new User { Id = 13 } }, "t").Author);
    }

    [Fact]
    public void MapComment_NullUser_YieldsEmptyAuthor()
        => Assert.Equal("", ClickUpClient.MapComment(new Comment { Id = "c1" }, "t").Author);

    [Fact]
    public void MapComment_MissingIdAndText_DefaultToEmpty()
    {
        var mapped = ClickUpClient.MapComment(new Comment(), "t");

        Assert.Equal("", mapped.Id);
        Assert.Equal("", mapped.Text);
        Assert.False(mapped.Resolved); // resolved absent (null) ⇒ false
    }

    [Fact]
    public void MapComment_AbsentOrUnparseableDate_YieldsNull()
    {
        Assert.Null(ClickUpClient.MapComment(new Comment { Date = null }, "t").DateMs);
        Assert.Null(ClickUpClient.MapComment(new Comment { Date = "not-a-number" }, "t").DateMs);
    }

    [Fact]
    public void MapComment_NullTaskId_LeavesAttributionNull()
        => Assert.Null(ClickUpClient.MapComment(new Comment { Id = "c1" }, taskId: null).TaskId);

    // ── Structured mention-block mapping (#167) ────────────────────────────────

    [Fact]
    public void MapComment_ExtractsMentionedUserIds_DistinctIgnoringPlainAndZeroAndUserless()
    {
        var c = new Comment
        {
            Id = "c1",
            CommentText = "@Ben and @Alice please look",
            CommentProp = new List<CommentBlock>
            {
                new() { Text = "hey " },                                              // plain run → ignored
                new() { Text = "@Ben", Type = "tag", User = new User { Id = 7 } },
                new() { Text = "@Alice", Type = "tag", User = new User { Id = 9 } },
                new() { Text = "@Ben", Type = "tag", User = new User { Id = 7 } },     // duplicate → deduped
                new() { Text = "@nobody", Type = "tag", User = new User { Id = 0 } },  // id 0 → ignored
                new() { Text = "@ghost", Type = "tag" },                              // null user → ignored
            },
        };

        Assert.Equal(new long[] { 7, 9 }, ClickUpClient.MapComment(c, "task-1").MentionedUserIds);
    }

    [Fact]
    public void MapComment_NoBlocks_YieldsEmptyMentionedUserIds()
        => Assert.Empty(ClickUpClient.MapComment(new Comment { Id = "c1" }, "t").MentionedUserIds);

    [Fact]
    public void MapMentionedUserIds_NullBlocks_YieldsEmpty()
        => Assert.Empty(ClickUpClient.MapMentionedUserIds(null));

    // Pins the wire contract: runs the real Kiota deserializer over a captured ClickUp v2 comment payload
    // to prove the structured `comment` blocks (and their `user.id`) land in `CommentProp` where the
    // mapper reads them — i.e. the curated spec faithfully models ClickUp's mention-block shape (#167).
    private static Comment Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var node = new JsonParseNode(doc.RootElement);
        return node.GetObjectValue(Comment.CreateFromDiscriminatorValue)!;
    }

    [Fact]
    public void MapComment_DeserializesStructuredMentionBlocks_FromRealPayloadShape()
    {
        // Shape captured from GET /v2/task/{id}/comment: a plain run followed by an @-mention tag block
        // carrying the mentioned member as { user: { id, username } }, alongside the flat comment_text.
        const string json = """
            {
              "id": "90120076543210",
              "comment": [
                { "text": "cc " },
                { "text": "@Ben Seymour", "type": "tag", "user": { "id": 183, "username": "Ben Seymour" } },
                { "text": " please review" }
              ],
              "comment_text": "cc @Ben Seymour please review",
              "user": { "id": 42, "username": "Alex Kim" },
              "date": "1699000000000",
              "resolved": false,
              "reply_count": "2"
            }
            """;

        var mapped = ClickUpClient.MapComment(Parse(json), "task-1");

        Assert.Equal(new long[] { 183 }, mapped.MentionedUserIds);
        Assert.Equal("cc @Ben Seymour please review", mapped.Text);
        Assert.Equal("Alex Kim", mapped.Author); // the comment author, not the mentioned user
        Assert.Equal(2, mapped.ReplyCount);       // ClickUp returns reply_count as a string (#327)
    }
}
