using ClickUpTodo.ClickUp;
using ClickUpTodo.ClickUp.Generated.Models;

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

        Assert.Equal(new CommentItem("c1", "Ben", 1_699_000_000_000, "Looks good.", true, "task-9"), mapped);
    }

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
}
