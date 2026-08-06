using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure reply-target picker model (issue #330): which comments are offered as reply
/// targets, their order, and their labels. The Terminal.Gui glue (the picker overlay, composer reply
/// mode) is verified by build + <c>tui-validate</c> per the repo's TUI rule; this locks the decisions it
/// delegates.
/// </summary>
public sealed class CommentReplyModelTests
{
    private static CommentItem Comment(string id, string author = "author", long dateMs = 100, string text = "body", int replyCount = 0)
        => new(id, author, dateMs, text, Resolved: false, TaskId: "t1", ReplyCount: replyCount);

    [Fact]
    public void Targets_OffersTopLevelComments_NewestFirst()
    {
        var comments = new List<CommentItem>
        {
            Comment("c1", dateMs: 100),
            Comment("c2", dateMs: 300),
            Comment("c3", dateMs: 200),
        };

        var targets = CommentReplyModel.Targets(comments);

        Assert.Equal(["c2", "c3", "c1"], targets.Select(t => t.CommentId)); // 300, 200, 100
    }

    [Fact]
    public void Targets_ExcludesPendingAndEmptyIdComments()
    {
        var comments = new List<CommentItem>
        {
            Comment("c1"),
            Comment(CommentComposerModel.PendingIdPrefix + "9"), // optimistic, not yet confirmed
            Comment(""),                                          // degenerate
        };

        var targets = CommentReplyModel.Targets(comments);

        Assert.Equal(["c1"], targets.Select(t => t.CommentId));
    }

    [Fact]
    public void Targets_TiebreaksEqualDatesByIdDescending()
    {
        var comments = new List<CommentItem> { Comment("a", dateMs: 100), Comment("b", dateMs: 100) };

        var targets = CommentReplyModel.Targets(comments);

        Assert.Equal(["b", "a"], targets.Select(t => t.CommentId));
    }

    [Fact]
    public void HasTargets_TrueOnlyWhenAReplyableCommentExists()
    {
        Assert.False(CommentReplyModel.HasTargets([]));
        Assert.False(CommentReplyModel.HasTargets([Comment(CommentComposerModel.PendingIdPrefix + "1")]));
        Assert.True(CommentReplyModel.HasTargets([Comment("c1")]));
    }

    [Fact]
    public void Target_CarriesAuthorForTheComposerTitle()
    {
        var target = CommentReplyModel.Targets([Comment("c1", author: "Ada Lovelace")]).Single();

        Assert.Equal("c1", target.CommentId);
        Assert.Equal("Ada Lovelace", target.Author);
    }

    [Fact]
    public void Label_CombinesAuthorAndSnippet()
        => Assert.Equal("Ada · Ship it", CommentReplyModel.Label(Comment("c1", author: "Ada", text: "Ship it")));

    [Fact]
    public void Label_AppendsReplyCountWhenThreadNonEmpty()
    {
        Assert.Equal("Ada · Hi (1 reply)", CommentReplyModel.Label(Comment("c1", author: "Ada", text: "Hi", replyCount: 1)));
        Assert.Equal("Ada · Hi (3 replies)", CommentReplyModel.Label(Comment("c1", author: "Ada", text: "Hi", replyCount: 3)));
    }

    [Fact]
    public void Label_FallsBackToAuthorOnlyForABlankBody()
        => Assert.Equal("Ada", CommentReplyModel.Label(Comment("c1", author: "Ada", text: "   ")));

    [Fact]
    public void DisplayAuthor_UsesYouForABlankAuthor()
    {
        Assert.Equal("(you)", CommentReplyModel.DisplayAuthor(""));
        Assert.Equal("(you)", CommentReplyModel.DisplayAuthor("   "));
        Assert.Equal("Ada", CommentReplyModel.DisplayAuthor("  Ada  "));
    }

    [Fact]
    public void Snippet_CollapsesNewlinesToOneLine()
        => Assert.Equal("line one line two", CommentReplyModel.Snippet("line one\n\nline two"));

    [Fact]
    public void Snippet_EllipsisesPastTheCap()
    {
        var text = new string('x', CommentReplyModel.SnippetMaxLength + 20);

        var snippet = CommentReplyModel.Snippet(text);

        Assert.Equal(CommentReplyModel.SnippetMaxLength, snippet.Length);
        Assert.EndsWith("…", snippet);
    }

    [Fact]
    public void Snippet_KeepsAShortBodyVerbatim()
        => Assert.Equal("short", CommentReplyModel.Snippet("short"));
}
