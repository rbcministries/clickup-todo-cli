using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pins the pure, CI-testable surface of the feed screen (#114): the empty-state copy, the
/// mentions-only filter, the empty-message selection, and the row-building badge attachment. The
/// Terminal.Gui view is not instantiated (the suite never calls <c>Application.Init</c>), matching the
/// repo's pattern of asserting only the framework-free logic of a screen.
/// </summary>
public sealed class NotificationsFeedScreenTests
{
    private static CommentItem Comment(string id, bool mentionsMe)
        => new(id, "Author", DateMs: 1_751_476_320_000, Text: "body", Resolved: false, TaskId: "t1", MentionsMe: mentionsMe);

    private static CommentItem Comment(string id, string? taskId)
        => new(id, "Author", DateMs: 1_751_476_320_000, Text: "body", Resolved: false, TaskId: taskId);

    [Fact]
    public void EmptyStatePlaceholder_IsNonEmpty()
        => Assert.False(string.IsNullOrWhiteSpace(NotificationsFeedScreen.EmptyStatePlaceholder));

    [Fact]
    public void EmptyStatePlaceholder_DescribesTheFeedAndTheWayBack()
    {
        var text = NotificationsFeedScreen.EmptyStatePlaceholder;

        Assert.Contains("mention", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("comment", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Esc", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoMentionsPlaceholder_ExplainsTheFilterAndTheWayBack()
    {
        var text = NotificationsFeedScreen.NoMentionsPlaceholder;

        Assert.Contains("mention", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("F3", text, StringComparison.Ordinal);
        Assert.Contains("Esc", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Filter_Off_ReturnsWholeFeed()
    {
        var feed = new[] { Comment("c1", mentionsMe: false), Comment("c2", mentionsMe: true) };

        var result = NotificationsFeedScreen.Filter(feed, mentionsOnly: false);

        Assert.Same(feed, result);
    }

    [Fact]
    public void Filter_On_KeepsOnlyMentions_PreservingOrder()
    {
        var feed = new[]
        {
            Comment("c1", mentionsMe: true),
            Comment("c2", mentionsMe: false),
            Comment("c3", mentionsMe: true),
        };

        var result = NotificationsFeedScreen.Filter(feed, mentionsOnly: true);

        Assert.Equal(new[] { "c1", "c3" }, result.Select(c => c.Id));
    }

    [Fact]
    public void EmptyMessage_MentionsOnly_WithComments_UsesNoMentionsCopy()
        => Assert.Equal(
            NotificationsFeedScreen.NoMentionsPlaceholder,
            NotificationsFeedScreen.EmptyMessage(mentionsOnly: true, feedHasAnyComments: true));

    [Fact]
    public void EmptyMessage_MentionsOnly_NoComments_UsesNoCommentsCopy()
        => Assert.Equal(
            NotificationsFeedScreen.EmptyStatePlaceholder,
            NotificationsFeedScreen.EmptyMessage(mentionsOnly: true, feedHasAnyComments: false));

    [Fact]
    public void EmptyMessage_AllComments_UsesNoCommentsCopy()
        => Assert.Equal(
            NotificationsFeedScreen.EmptyStatePlaceholder,
            NotificationsFeedScreen.EmptyMessage(mentionsOnly: false, feedHasAnyComments: true));

    [Fact]
    public void MentionCoverageNote_NamesTheAutomationAndCitesTheDoc()
    {
        var note = NotificationsFeedScreen.MentionCoverageNote;

        Assert.False(string.IsNullOrWhiteSpace(note));
        Assert.Contains("mention", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("automation", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("per-Space", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("docs/mention-assignee-automation.md", note, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyStatePlaceholder_CarriesTheCoverageNote()
        => Assert.Contains(NotificationsFeedScreen.MentionCoverageNote,
            NotificationsFeedScreen.EmptyStatePlaceholder, StringComparison.Ordinal);

    [Fact]
    public void NoMentionsPlaceholder_CarriesTheCoverageNote()
        => Assert.Contains(NotificationsFeedScreen.MentionCoverageNote,
            NotificationsFeedScreen.NoMentionsPlaceholder, StringComparison.Ordinal);

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void EmptyMessage_AlwaysCarriesTheCoverageNote(bool mentionsOnly, bool feedHasAnyComments)
        => Assert.Contains(NotificationsFeedScreen.MentionCoverageNote,
            NotificationsFeedScreen.EmptyMessage(mentionsOnly, feedHasAnyComments), StringComparison.Ordinal);

    [Fact]
    public void BuildRows_AttachesABadgeOnlyToMentionRows()
    {
        var feed = new[] { Comment("c1", mentionsMe: false), Comment("c2", mentionsMe: true) };

        var (text, badges, keys) = NotificationsFeedScreen.BuildRows(feed);

        Assert.Equal(2, text.Count);
        Assert.Equal(2, keys.Count);
        Assert.Empty(badges[0]);      // plain comment — no mention chip
        Assert.Single(badges[1]);     // mention — one coloured chip span
    }

    [Fact]
    public void BuildRows_Empty_ProducesEmptyArrays()
    {
        var (text, badges, keys) = NotificationsFeedScreen.BuildRows([]);

        Assert.Empty(text);
        Assert.Empty(badges);
        Assert.Empty(keys);
    }

    [Fact]
    public void SelectedTaskId_ReturnsRowTaskId_ForValidIndex()
    {
        var rows = new[] { Comment("c1", taskId: "alpha"), Comment("c2", taskId: "beta") };

        Assert.Equal("alpha", NotificationsFeedScreen.SelectedTaskId(rows, 0));
        Assert.Equal("beta", NotificationsFeedScreen.SelectedTaskId(rows, 1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(99)]
    public void SelectedTaskId_OutOfRange_ReturnsNull(int index)
    {
        var rows = new[] { Comment("c1", taskId: "alpha"), Comment("c2", taskId: "beta") };

        Assert.Null(NotificationsFeedScreen.SelectedTaskId(rows, index));
    }

    [Fact]
    public void SelectedTaskId_EmptyRows_ReturnsNull()
        => Assert.Null(NotificationsFeedScreen.SelectedTaskId([], 0));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SelectedTaskId_RowWithoutTaskId_ReturnsNull(string? taskId)
    {
        var rows = new[] { Comment("c1", taskId) };

        Assert.Null(NotificationsFeedScreen.SelectedTaskId(rows, 0));
    }

    [Fact]
    public void SelectedTaskId_ResolvesAgainstFilteredRows_UnderMentionsOnly()
    {
        // The rows Enter indexes into are the F3-filtered rows, not the raw feed. With mentions-only on,
        // index 0 must resolve to the first *mention's* task — not the first raw comment's.
        var feed = new[]
        {
            new CommentItem("c1", "A", 3, "b", false, TaskId: "not-a-mention", MentionsMe: false),
            new CommentItem("c2", "A", 2, "b", false, TaskId: "mention-task", MentionsMe: true),
        };

        var filtered = NotificationsFeedScreen.Filter(feed, mentionsOnly: true);

        Assert.Equal("mention-task", NotificationsFeedScreen.SelectedTaskId(filtered, 0));
        Assert.Null(NotificationsFeedScreen.SelectedTaskId(filtered, 1)); // only one mention row
    }
}
