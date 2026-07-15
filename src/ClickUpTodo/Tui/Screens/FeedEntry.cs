using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// One row in the merged feed (#117): either a <see cref="CommentItem"/> (a mention/comment, #109) or
/// an <see cref="ActivityItem"/> (a recently-updated assigned task). The feed screen renders comments
/// always and merges activity in only when its <c>F6</c> "show activity" state is on; unifying both as
/// a <see cref="FeedEntry"/> lets the screen sort, select, and render them as one newest-first list.
/// The projected fields (<see cref="Id"/>, <see cref="DateMs"/>, <see cref="TaskId"/>,
/// <see cref="MentionsMe"/>) are exactly what the sort / selection-tracking / open-task logic needs, so
/// those stay a pure function of the entry regardless of which source it came from.
/// </summary>
public sealed record FeedEntry
{
    /// <summary>The entry's stable id: a comment id, or an <see cref="ActivityItem.Id"/>
    /// (<c>"activity:" + taskId</c>). The two id spaces are disjoint, so this uniquely keys either kind
    /// for de-dup and cross-refresh selection tracking.</summary>
    public required string Id { get; init; }

    /// <summary>The sort key: a comment's date, or a task's <see cref="TaskItem.UpdatedMs"/>. Null sorts
    /// last (see the screen's merge), matching the comment feed's undated-last rule.</summary>
    public required long? DateMs { get; init; }

    /// <summary>The task this row opens on Enter (#115): a comment's <see cref="CommentItem.TaskId"/> or
    /// an activity row's <see cref="ActivityItem.TaskId"/>. Null only for an unattributed comment.</summary>
    public string? TaskId { get; init; }

    /// <summary>Whether this row mentions the current user (#113). Always false for an activity row (a
    /// task update is not a mention), so the mentions-only filter naturally excludes activity.</summary>
    public bool MentionsMe { get; init; }

    /// <summary>The comment this row wraps, or null when it is an activity row.</summary>
    public CommentItem? Comment { get; init; }

    /// <summary>The activity item this row wraps, or null when it is a comment row.</summary>
    public ActivityItem? Activity { get; init; }

    /// <summary>True when this is a recent-activity row (as opposed to a comment).</summary>
    public bool IsActivity => Activity is not null;

    /// <summary>Wraps a comment as a feed entry, projecting the fields the feed sorts/selects on.</summary>
    public static FeedEntry Of(CommentItem comment) => new()
    {
        Id = comment.Id,
        DateMs = comment.DateMs,
        TaskId = comment.TaskId,
        MentionsMe = comment.MentionsMe,
        Comment = comment,
    };

    /// <summary>Wraps a recent-activity item as a feed entry (never a mention).</summary>
    public static FeedEntry Of(ActivityItem activity) => new()
    {
        Id = activity.Id,
        DateMs = activity.UpdatedMs,
        TaskId = activity.TaskId,
        MentionsMe = false,
        Activity = activity,
    };
}
