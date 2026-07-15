using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui.Screens;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pins the pure, CI-testable surface of the feed screen (#114, #117): the empty-state copy, the
/// comment/activity merge (<c>BuildEntries</c>), the empty-message selection, the row-building badge
/// attachment, the title indicators, and cross-refresh selection tracking. The Terminal.Gui view is not
/// instantiated (the suite never calls <c>Application.Init</c>), matching the repo's pattern of
/// asserting only the framework-free logic of a screen.
/// </summary>
public sealed class NotificationsFeedScreenTests
{
    private static CommentItem Comment(string id, bool mentionsMe, long dateMs = 1_751_476_320_000)
        => new(id, "Author", dateMs, "body", Resolved: false, TaskId: "t1", MentionsMe: mentionsMe);

    private static CommentItem Comment(string id, string? taskId, long dateMs = 1_751_476_320_000)
        => new(id, "Author", dateMs, "body", Resolved: false, TaskId: taskId);

    private static ActivityItem Activity(string taskId, long? updatedMs)
        => new(ActivityItem.IdPrefix + taskId, taskId, $"Task {taskId}", "in progress", updatedMs);

    private static FeedEntry Entry(CommentItem c) => FeedEntry.Of(c);
    private static FeedEntry Entry(ActivityItem a) => FeedEntry.Of(a);

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

    // ── BuildEntries: comment/activity merge (#117) ─────────────────────────────

    [Fact]
    public void BuildEntries_CommentsOnly_NewestFirst_TiesByIdOrdinal()
    {
        var comments = new[]
        {
            Comment("a", mentionsMe: false, dateMs: 100),
            Comment("b", mentionsMe: false, dateMs: 300),
            Comment("c", mentionsMe: false, dateMs: 200),
        };

        var entries = NotificationsFeedScreen.BuildEntries(comments, [], mentionsOnly: false, showActivity: false);

        Assert.Equal(new[] { "b", "c", "a" }, entries.Select(e => e.Id));
        Assert.All(entries, e => Assert.False(e.IsActivity));
    }

    [Fact]
    public void BuildEntries_MentionsOnly_KeepsOnlyMentionComments()
    {
        var comments = new[]
        {
            Comment("c1", mentionsMe: true, dateMs: 300),
            Comment("c2", mentionsMe: false, dateMs: 200),
            Comment("c3", mentionsMe: true, dateMs: 100),
        };

        var entries = NotificationsFeedScreen.BuildEntries(comments, [], mentionsOnly: true, showActivity: false);

        Assert.Equal(new[] { "c1", "c3" }, entries.Select(e => e.Id));
    }

    [Fact]
    public void BuildEntries_ShowActivity_MergesActivityIntoTheFeedNewestFirst()
    {
        var comments = new[] { Comment("c1", mentionsMe: false, dateMs: 100), Comment("c2", mentionsMe: false, dateMs: 400) };
        var activity = new[] { Activity("t1", 300), Activity("t2", 50) };

        var entries = NotificationsFeedScreen.BuildEntries(comments, activity, mentionsOnly: false, showActivity: true);

        // Interleaved strictly by date: c2(400), activity t1(300), c1(100), activity t2(50).
        Assert.Equal(new[] { "c2", "activity:t1", "c1", "activity:t2" }, entries.Select(e => e.Id));
        Assert.True(entries[1].IsActivity);
        Assert.Equal("t1", entries[1].TaskId);
    }

    [Fact]
    public void BuildEntries_ShowActivityOff_DropsActivity()
    {
        var comments = new[] { Comment("c1", mentionsMe: false, dateMs: 100) };
        var activity = new[] { Activity("t1", 300) };

        var entries = NotificationsFeedScreen.BuildEntries(comments, activity, mentionsOnly: false, showActivity: false);

        Assert.Equal(new[] { "c1" }, entries.Select(e => e.Id));
    }

    [Fact]
    public void BuildEntries_MentionsOnly_SuppressesActivity_EvenWhenShowActivityOn()
    {
        // Mentions-only is the narrowest view; a task update is not a mention, so activity never shows.
        var comments = new[] { Comment("c1", mentionsMe: true, dateMs: 100) };
        var activity = new[] { Activity("t1", 900) };

        var entries = NotificationsFeedScreen.BuildEntries(comments, activity, mentionsOnly: true, showActivity: true);

        Assert.Equal(new[] { "c1" }, entries.Select(e => e.Id));
        Assert.DoesNotContain(entries, e => e.IsActivity);
    }

    [Theory]
    [InlineData(false, false, "Feed — mentions & comments")]
    [InlineData(true, false, "Feed — mentions only")]
    public void TitleFor_ReflectsMentionsBase(bool mentionsOnly, bool showCompleted, string expected)
        => Assert.Equal(expected, NotificationsFeedScreen.TitleFor(mentionsOnly, showCompleted, showActivity: false));

    [Theory]
    [InlineData(false, true, false, "Feed — mentions & comments (+completed)")]
    [InlineData(true, true, false, "Feed — mentions only (+completed)")]
    [InlineData(false, false, true, "Feed — mentions & comments (+activity)")]
    [InlineData(false, true, true, "Feed — mentions & comments (+completed) (+activity)")]
    public void TitleFor_ReflectsCompletedAndActivitySuffixes(
        bool mentionsOnly, bool showCompleted, bool showActivity, string expected)
        => Assert.Equal(expected, NotificationsFeedScreen.TitleFor(mentionsOnly, showCompleted, showActivity));

    [Fact]
    public void TitleFor_MentionsOnly_SuppressesTheActivitySuffix()
    {
        // Activity can't be visible under mentions-only, so its title suffix must not appear either.
        Assert.Equal("Feed — mentions only",
            NotificationsFeedScreen.TitleFor(mentionsOnly: true, showCompleted: false, showActivity: true));
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
    public void BuildRows_AttachesTheRightChipPerRowKind()
    {
        var entries = new[]
        {
            Entry(Comment("c1", mentionsMe: false)),
            Entry(Comment("c2", mentionsMe: true)),
            Entry(Activity("t3", 1)),
        };

        var (text, badges, keys) = NotificationsFeedScreen.BuildRows(entries);

        Assert.Equal(3, text.Count);
        Assert.Equal(3, keys.Count);
        Assert.Empty(badges[0]);   // plain comment — no chip
        Assert.Single(badges[1]);  // mention — one coloured chip span
        Assert.Single(badges[2]);  // activity — one (differently-coloured) chip span
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
        var rows = new[] { Entry(Comment("c1", taskId: "alpha")), Entry(Activity("beta", 1)) };

        Assert.Equal("alpha", NotificationsFeedScreen.SelectedTaskId(rows, 0));
        Assert.Equal("beta", NotificationsFeedScreen.SelectedTaskId(rows, 1)); // activity rows open their task too
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(99)]
    public void SelectedTaskId_OutOfRange_ReturnsNull(int index)
    {
        var rows = new[] { Entry(Comment("c1", taskId: "alpha")), Entry(Comment("c2", taskId: "beta")) };

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
        var rows = new[] { Entry(Comment("c1", taskId)) };

        Assert.Null(NotificationsFeedScreen.SelectedTaskId(rows, 0));
    }

    [Fact]
    public void SelectedTaskId_ResolvesAgainstFilteredRows_UnderMentionsOnly()
    {
        // The rows Enter indexes into are the built (filtered/merged) rows, not the raw feed. With
        // mentions-only on, index 0 must resolve to the first *mention's* task — not the first raw comment's.
        var comments = new[]
        {
            new CommentItem("c1", "A", 3, "b", false, TaskId: "not-a-mention", MentionsMe: false),
            new CommentItem("c2", "A", 2, "b", false, TaskId: "mention-task", MentionsMe: true),
        };

        var rows = NotificationsFeedScreen.BuildEntries(comments, [], mentionsOnly: true, showActivity: false);

        Assert.Equal("mention-task", NotificationsFeedScreen.SelectedTaskId(rows, 0));
        Assert.Null(NotificationsFeedScreen.SelectedTaskId(rows, 1)); // only one mention row
    }

    // --- ResolveSelection: selection follows the same entry across a feed swap (#123) -------------

    [Fact]
    public void ResolveSelection_FollowsTheSameEntry_WhenNewerRowsArePrepended()
    {
        // A refresh prepends "c0" (newest-first), pushing the selected "c2" down a row. The selection
        // must follow the entry, not stay on the old index (which would slide onto "c1").
        var rows = new[] { Entry(Comment("c0", "t1")), Entry(Comment("c1", "t1")), Entry(Comment("c2", "t1")) };

        Assert.Equal(2, NotificationsFeedScreen.ResolveSelection(rows, selectedId: "c2", previousIndex: 1));
    }

    [Fact]
    public void ResolveSelection_TracksAnActivityEntryById()
    {
        var rows = new[] { Entry(Comment("c0", "t1")), Entry(Activity("t9", 1)) };

        Assert.Equal(1, NotificationsFeedScreen.ResolveSelection(rows, selectedId: "activity:t9", previousIndex: 0));
    }

    [Fact]
    public void ResolveSelection_FallsBackToClampedPriorIndex_WhenSelectedEntryIsGone()
    {
        var rows = new[] { Entry(Comment("a", "t1")), Entry(Comment("b", "t1")) };

        Assert.Equal(1, NotificationsFeedScreen.ResolveSelection(rows, selectedId: "gone", previousIndex: 5));
        Assert.Equal(0, NotificationsFeedScreen.ResolveSelection(rows, selectedId: "gone", previousIndex: null));
    }

    [Fact]
    public void ResolveSelection_EmptyFeed_ReturnsNegativeOne()
        => Assert.Equal(-1, NotificationsFeedScreen.ResolveSelection([], selectedId: "c1", previousIndex: 0));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveSelection_EmptyOrAbsentSelectedId_NeverMatches_FallsBackToIndex(string? selectedId)
    {
        var rows = new[] { Entry(Comment("", "t1")), Entry(Comment("c2", "t1")) };

        Assert.Equal(1, NotificationsFeedScreen.ResolveSelection(rows, selectedId, previousIndex: 1));
    }
}
