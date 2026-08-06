using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Unit tests for the pure comment-composer model (issue #216): key→action routing, the empty-body
/// gate + normalization, and the optimistic append / reconcile / revert list transforms. The
/// Terminal.Gui glue in <c>TaskDetailScreen</c> is verified by build + reasoning + <c>tui-validate</c>
/// per the repo's TUI rule; this locks the decisions it delegates.
/// </summary>
public sealed class CommentComposerModelTests
{
    [Theory]
    [InlineData(CommentComposerModel.ComposerKey.Submit, CommentComposerModel.ComposerAction.Post)]
    [InlineData(CommentComposerModel.ComposerKey.Cancel, CommentComposerModel.ComposerAction.Cancel)]
    [InlineData(CommentComposerModel.ComposerKey.Other, CommentComposerModel.ComposerAction.PassThrough)]
    public void Route_MapsEachKeyToItsAction(
        CommentComposerModel.ComposerKey key, CommentComposerModel.ComposerAction expected)
        => Assert.Equal(expected, CommentComposerModel.Route(key));

    [Theory]
    [InlineData("hello", true)]
    [InlineData("  trimmed later  ", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("\n\t  \n", false)]
    [InlineData(null, false)]
    public void IsPostable_GatesEmptyAndWhitespaceBodies(string? text, bool expected)
        => Assert.Equal(expected, CommentComposerModel.IsPostable(text));

    [Theory]
    [InlineData("  hi  ", "hi")]
    [InlineData("\n\nline\n\n", "line")]
    [InlineData(null, "")]
    [InlineData("keep\ninner\nnewlines", "keep\ninner\nnewlines")]
    public void Normalize_TrimsSurroundingWhitespaceOnly(string? text, string expected)
        => Assert.Equal(expected, CommentComposerModel.Normalize(text));

    [Fact]
    public void Provisional_CarriesTrimmedTextEmptyAuthorAndStampedDate()
    {
        var p = CommentComposerModel.Provisional("__pending__1", "  a comment  ", 1_700_000_000_000);

        Assert.Equal("__pending__1", p.Id);
        Assert.Equal("", p.Author);
        Assert.Equal(1_700_000_000_000, p.DateMs);
        Assert.Equal("a comment", p.Text); // normalized
        Assert.False(p.Resolved);
    }

    [Fact]
    public void Append_AddsProvisionalAtTheEnd_WithoutMutatingInput()
    {
        var existing = new List<CommentItem>
        {
            new("c1", "Ada", 100, "first", false),
            new("c2", "Alan", 200, "second", false),
        };
        var provisional = CommentComposerModel.Provisional("__pending__1", "third", 300);

        var result = CommentComposerModel.Append(existing, provisional);

        Assert.Equal(3, result.Count);
        Assert.Equal("__pending__1", result[^1].Id);
        Assert.Equal(2, existing.Count); // input untouched
    }

    [Fact]
    public void Reconcile_ReplacesProvisionalWithConfirmed_InPlace()
    {
        var provisional = CommentComposerModel.Provisional("__pending__1", "hi", 300);
        var comments = new List<CommentItem>
        {
            new("c1", "Ada", 100, "first", false),
            provisional,
        };
        var confirmed = new CommentItem("real-99", "", 305, "hi", false, "task1");

        var result = CommentComposerModel.Reconcile(comments, "__pending__1", confirmed);

        Assert.Equal(2, result.Count);
        Assert.Equal("c1", result[0].Id); // untouched
        Assert.Equal("real-99", result[1].Id); // provisional replaced by server-confirmed
        Assert.Equal(2, comments.Count); // input untouched
    }

    [Fact]
    public void Reconcile_IsNoOp_WhenProvisionalNoLongerPresent()
    {
        // A background refresh replaced the list mid-post, so the provisional is gone: reconcile must
        // not resurrect or duplicate it — the next refresh re-pulls the real posted comment.
        var comments = new List<CommentItem> { new("c1", "Ada", 100, "first", false) };
        var confirmed = new CommentItem("real-99", "", 305, "hi", false);

        var result = CommentComposerModel.Reconcile(comments, "__pending__1", confirmed);

        Assert.Single(result);
        Assert.Equal("c1", result[0].Id);
        Assert.DoesNotContain(result, c => c.Id == "real-99");
    }

    [Fact]
    public void Revert_DropsOnlyTheProvisional()
    {
        var provisional = CommentComposerModel.Provisional("__pending__1", "hi", 300);
        var comments = new List<CommentItem>
        {
            new("c1", "Ada", 100, "first", false),
            provisional,
            new("c2", "Alan", 200, "second", false),
        };

        var result = CommentComposerModel.Revert(comments, "__pending__1");

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, c => c.Id == "__pending__1");
        Assert.Equal(3, comments.Count); // input untouched
    }

    [Fact]
    public void Revert_IsNoOp_WhenProvisionalAbsent()
    {
        var comments = new List<CommentItem> { new("c1", "Ada", 100, "first", false) };

        var result = CommentComposerModel.Revert(comments, "__pending__1");

        Assert.Single(result);
        Assert.Equal("c1", result[0].Id);
    }

    // ── Pending-id helpers (#330) ──────────────────────────────────────────────

    [Theory]
    [InlineData("__pending__1", true)]
    [InlineData("__pending__42", true)]
    [InlineData("real-99", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPending_RecognisesTheProvisionalSentinel(string? id, bool expected)
        => Assert.Equal(expected, CommentComposerModel.IsPending(id));

    [Fact]
    public void PendingIdPrefix_IsWhatProvisionalUses()
        => Assert.StartsWith(CommentComposerModel.PendingIdPrefix, CommentComposerModel.Provisional("__pending__7", "x", 1).Id);

    // ── Reply into a thread (#330) ─────────────────────────────────────────────

    [Fact]
    public void ProvisionalReply_StampsParentTaskAndTrimsText()
    {
        var r = CommentComposerModel.ProvisionalReply("__pending__1", "  hi there  ", 500, parentCommentId: "c1", taskId: "t1");

        Assert.Equal("__pending__1", r.Id);
        Assert.Equal("", r.Author);
        Assert.Equal(500, r.DateMs);
        Assert.Equal("hi there", r.Text); // normalized
        Assert.Equal("c1", r.ParentCommentId);
        Assert.Equal("t1", r.TaskId);
        Assert.False(r.Resolved);
    }

    [Fact]
    public void AppendReply_NestsUnderParentAndBumpsCount_WithoutMutatingInput()
    {
        var comments = new List<CommentItem>
        {
            new("c1", "Ada", 100, "first", false, "t1", ReplyCount: 1, Replies: new List<CommentItem> { new("r0", "Grace", 110, "old reply", false) }),
            new("c2", "Alan", 200, "second", false, "t1"),
        };
        var provisional = CommentComposerModel.ProvisionalReply("__pending__1", "new reply", 300, "c1", "t1");

        var result = CommentComposerModel.AppendReply(comments, "c1", provisional);

        var c1 = result.Single(c => c.Id == "c1");
        Assert.Equal(["r0", "__pending__1"], c1.Replies.Select(r => r.Id)); // appended after existing
        Assert.Equal(2, c1.ReplyCount);
        Assert.Empty(result.Single(c => c.Id == "c2").Replies); // untouched
        Assert.Single(comments[0].Replies); // input untouched
        Assert.Equal(1, comments[0].ReplyCount);
    }

    [Fact]
    public void AppendReply_IsNoOp_WhenParentNoLongerPresent()
    {
        // A refresh replaced the list mid-post and the parent is gone: the reply write still happens,
        // but there's nothing to nest under, so the list is returned unchanged (the next refresh pulls it).
        var comments = new List<CommentItem> { new("c2", "Alan", 200, "second", false, "t1") };
        var provisional = CommentComposerModel.ProvisionalReply("__pending__1", "orphan", 300, "c1", "t1");

        var result = CommentComposerModel.AppendReply(comments, "c1", provisional);

        Assert.Single(result);
        Assert.Equal("c2", result[0].Id);
        Assert.DoesNotContain(result.SelectMany(c => c.Replies), r => r.Id == "__pending__1");
    }

    [Fact]
    public void ReconcileReply_ReplacesProvisionalInThreadAndRestampsLinkage()
    {
        var provisional = CommentComposerModel.ProvisionalReply("__pending__1", "hi", 300, "c1", "t1");
        var comments = new List<CommentItem>
        {
            new("c1", "Ada", 100, "first", false, "t1", ReplyCount: 1, Replies: new List<CommentItem> { provisional }),
        };
        // The create-reply facade returns the parent id and task id as null; reconcile must re-stamp them.
        var confirmed = new CommentItem("real-reply", "", 305, "hi", false, TaskId: null, ParentCommentId: null);

        var result = CommentComposerModel.ReconcileReply(comments, "c1", "__pending__1", confirmed);

        var reply = result.Single(c => c.Id == "c1").Replies.Single();
        Assert.Equal("real-reply", reply.Id);
        Assert.Equal("c1", reply.ParentCommentId); // re-stamped
        Assert.Equal("t1", reply.TaskId);          // re-stamped from the provisional's task
        Assert.Equal(1, result[0].ReplyCount);     // count unchanged (replace in place)
    }

    [Fact]
    public void ReconcileReply_IsNoOp_WhenProvisionalOrParentGone()
    {
        var comments = new List<CommentItem> { new("c1", "Ada", 100, "first", false, "t1") };
        var confirmed = new CommentItem("real-reply", "", 305, "hi", false);

        // Parent present but provisional gone (a refresh cleared the thread):
        var afterProvisionalGone = CommentComposerModel.ReconcileReply(comments, "c1", "__pending__1", confirmed);
        Assert.Empty(afterProvisionalGone.Single(c => c.Id == "c1").Replies);

        // Parent gone entirely:
        var afterParentGone = CommentComposerModel.ReconcileReply(comments, "missing", "__pending__1", confirmed);
        Assert.DoesNotContain(afterParentGone.SelectMany(c => c.Replies), r => r.Id == "real-reply");
    }

    [Fact]
    public void RevertReply_DropsProvisionalAndDecrementsCount()
    {
        var provisional = CommentComposerModel.ProvisionalReply("__pending__1", "hi", 300, "c1", "t1");
        var comments = new List<CommentItem>
        {
            new("c1", "Ada", 100, "first", false, "t1", ReplyCount: 2,
                Replies: new List<CommentItem> { new("r0", "Grace", 110, "old", false), provisional }),
        };

        var result = CommentComposerModel.RevertReply(comments, "c1", "__pending__1");

        var c1 = result.Single(c => c.Id == "c1");
        Assert.Equal(["r0"], c1.Replies.Select(r => r.Id));
        Assert.Equal(1, c1.ReplyCount);
        Assert.Equal(2, comments[0].Replies.Count); // input untouched
    }

    [Fact]
    public void RevertReply_ClampsReplyCountAtZero_IfItWasAlreadyZero()
    {
        // Defensive: a provisional reply present while the parent reports ReplyCount 0 (an inconsistent
        // state a refresh could leave) must not drive the count negative on revert.
        var provisional = CommentComposerModel.ProvisionalReply("__pending__1", "hi", 300, "c1", "t1");
        var comments = new List<CommentItem>
        {
            new("c1", "Ada", 100, "first", false, "t1", ReplyCount: 0,
                Replies: new List<CommentItem> { provisional }),
        };

        var result = CommentComposerModel.RevertReply(comments, "c1", "__pending__1");

        var c1 = result.Single(c => c.Id == "c1");
        Assert.Empty(c1.Replies);
        Assert.Equal(0, c1.ReplyCount); // clamped, not -1
    }

    [Fact]
    public void RevertReply_IsNoOp_WhenProvisionalAbsent()
    {
        var comments = new List<CommentItem>
        {
            new("c1", "Ada", 100, "first", false, "t1", ReplyCount: 1,
                Replies: new List<CommentItem> { new("r0", "Grace", 110, "old", false) }),
        };

        var result = CommentComposerModel.RevertReply(comments, "c1", "__pending__1");

        var c1 = result.Single(c => c.Id == "c1");
        Assert.Equal(["r0"], c1.Replies.Select(r => r.Id));
        Assert.Equal(1, c1.ReplyCount); // unchanged — nothing was dropped
    }
}
