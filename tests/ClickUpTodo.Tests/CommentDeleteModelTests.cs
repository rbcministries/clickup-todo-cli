using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure comment-delete picker model (issue #594, the deferred comment half of the
/// contextual-Delete slice #543): which comments are offered as delete targets (top-level + replies), their
/// order and labels, and the optimistic <see cref="CommentDeleteModel.Remove"/> transform. The Terminal.Gui
/// glue (the picker overlay, the confirm, the off-thread write) is verified by build + <c>tui-validate</c>
/// per the repo's TUI rule; this locks the decisions it delegates.
/// </summary>
public sealed class CommentDeleteModelTests
{
    private static CommentItem Comment(
        string id, string author = "author", long dateMs = 100, string text = "body",
        int replyCount = 0, IReadOnlyList<CommentItem>? replies = null)
        => new(id, author, dateMs, text, Resolved: false, TaskId: "t1", ReplyCount: replyCount,
            Replies: replies);

    private static CommentItem Reply(string id, string parentId, string author = "author", string text = "re")
        => new(id, author, DateMs: 100, text, Resolved: false, TaskId: "t1", ParentCommentId: parentId);

    [Fact]
    public void Targets_OffersTopLevelComments_NewestFirst()
    {
        var comments = new List<CommentItem>
        {
            Comment("c1", dateMs: 100),
            Comment("c2", dateMs: 300),
            Comment("c3", dateMs: 200),
        };

        var targets = CommentDeleteModel.Targets(comments);

        Assert.Equal(["c2", "c3", "c1"], targets.Select(t => t.CommentId)); // 300, 200, 100
    }

    [Fact]
    public void Targets_NestsRepliesUnderTheirParent_OldestFirst()
    {
        var comments = new List<CommentItem>
        {
            Comment("c2", dateMs: 300, replyCount: 2, replies:
            [
                Reply("r1", "c2"),
                Reply("r2", "c2"),
            ]),
            Comment("c1", dateMs: 100),
        };

        var targets = CommentDeleteModel.Targets(comments);

        // c2 (newest) then its replies in stored order, then c1.
        Assert.Equal(["c2", "r1", "r2", "c1"], targets.Select(t => t.CommentId));
        Assert.Equal([false, true, true, false], targets.Select(t => t.IsReply));
    }

    [Fact]
    public void Targets_ExcludesPendingAndEmptyIdComments_AtBothLevels()
    {
        var comments = new List<CommentItem>
        {
            Comment("c1", replyCount: 2, replies:
            [
                Reply("r1", "c1"),
                Reply(CommentComposerModel.PendingIdPrefix + "9", "c1"), // optimistic reply, not yet confirmed
            ]),
            Comment(CommentComposerModel.PendingIdPrefix + "8"),          // optimistic top-level
            Comment(""),                                                  // degenerate
        };

        var targets = CommentDeleteModel.Targets(comments);

        Assert.Equal(["c1", "r1"], targets.Select(t => t.CommentId));
    }

    [Fact]
    public void Targets_TiebreaksEqualDatesByIdDescending()
    {
        var comments = new List<CommentItem> { Comment("a", dateMs: 100), Comment("b", dateMs: 100) };

        var targets = CommentDeleteModel.Targets(comments);

        Assert.Equal(["b", "a"], targets.Select(t => t.CommentId));
    }

    [Fact]
    public void HasTargets_TrueOnlyWhenADeletableCommentExists()
    {
        Assert.False(CommentDeleteModel.HasTargets([]));
        Assert.False(CommentDeleteModel.HasTargets([Comment(CommentComposerModel.PendingIdPrefix + "1")]));
        Assert.True(CommentDeleteModel.HasTargets([Comment("c1")]));
        // A parent whose only content is a deletable reply still has a target (the reply, and the parent).
        Assert.True(CommentDeleteModel.HasTargets([Comment("c1", replyCount: 1, replies: [Reply("r1", "c1")])]));
    }

    [Fact]
    public void Target_CarriesAuthorAndLabel()
    {
        var target = CommentDeleteModel.Targets([Comment("c1", author: "Ada Lovelace", text: "Ship it")]).Single();

        Assert.Equal("c1", target.CommentId);
        Assert.Equal("Ada Lovelace", target.Author);
        Assert.Equal("Ada Lovelace · Ship it", target.Label);
        Assert.False(target.IsReply);
    }

    [Fact]
    public void TopLevelLabel_AppendsReplyCount_ReplyLabelIsArrowPrefixed()
    {
        var targets = CommentDeleteModel.Targets(
        [
            Comment("c1", author: "Ada", text: "Hi", replyCount: 1, replies: [Reply("r1", "c1", author: "Bo", text: "Sure")]),
        ]);

        Assert.Equal("Ada · Hi (1 reply)", targets[0].Label);      // top-level shows the thread size
        Assert.Equal("↳ Bo · Sure", targets[1].Label);             // a reply is arrow-prefixed, no count
    }

    [Fact]
    public void ReplyLabel_FallsBackToArrowAndAuthorForABlankBody()
    {
        var targets = CommentDeleteModel.Targets(
        [
            Comment("c1", replyCount: 1, replies: [Reply("r1", "c1", author: "Bo", text: "   ")]),
        ]);

        Assert.Equal("↳ Bo", targets[1].Label);
    }

    [Fact]
    public void BlankAuthorRendersAsYou()
    {
        var targets = CommentDeleteModel.Targets(
        [
            Comment("c1", author: "", text: "mine", replyCount: 1, replies: [Reply("r1", "c1", author: "")]),
        ]);

        Assert.Equal("(you)", targets[0].Author);
        Assert.Equal("(you) · mine (1 reply)", targets[0].Label);
        Assert.Equal("↳ (you) · re", targets[1].Label);
    }

    [Fact]
    public void Remove_DropsTopLevelCommentAndItsThread()
    {
        var comments = new List<CommentItem>
        {
            Comment("c1", replyCount: 1, replies: [Reply("r1", "c1")]),
            Comment("c2"),
        };

        var result = CommentDeleteModel.Remove(comments, "c1");

        Assert.Equal(["c2"], result.Select(c => c.Id));
    }

    [Fact]
    public void Remove_DropsANestedReply_AndDecrementsReplyCount()
    {
        var comments = new List<CommentItem>
        {
            Comment("c1", replyCount: 2, replies: [Reply("r1", "c1"), Reply("r2", "c1")]),
        };

        var result = CommentDeleteModel.Remove(comments, "r1");

        var parent = Assert.Single(result);
        Assert.Equal("c1", parent.Id);
        Assert.Equal(["r2"], parent.Replies.Select(r => r.Id));
        Assert.Equal(1, parent.ReplyCount);
    }

    [Fact]
    public void Remove_IsANoOpForAnAbsentId()
    {
        var comments = new List<CommentItem> { Comment("c1", replyCount: 1, replies: [Reply("r1", "c1")]) };

        var result = CommentDeleteModel.Remove(comments, "nope");

        Assert.Equal(["c1"], result.Select(c => c.Id));
        Assert.Equal(["r1"], result.Single().Replies.Select(r => r.Id));
        Assert.Equal(1, result.Single().ReplyCount);
    }

    [Fact]
    public void Remove_NeverLetsReplyCountGoNegative()
    {
        // A parent whose ReplyCount is out of step with Replies (defensive): removing the last reply floors at 0.
        var comments = new List<CommentItem> { Comment("c1", replyCount: 0, replies: [Reply("r1", "c1")]) };

        var result = CommentDeleteModel.Remove(comments, "r1");

        Assert.Empty(result.Single().Replies);
        Assert.Equal(0, result.Single().ReplyCount);
    }
}
