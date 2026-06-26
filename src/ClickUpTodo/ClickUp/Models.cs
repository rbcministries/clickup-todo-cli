namespace ClickUpTodo.ClickUp;

// Stable domain records the rest of the app consumes. The Kiota-generated client produces a
// different response type per endpoint (the spec uses inline schemas), so ClickUpClient maps all
// of them into these few shapes — insulating the TUI from regeneration churn.

/// <summary>The signed-in ClickUp user.</summary>
public sealed record ClickUpUser(long Id, string DisplayName);

/// <summary>An id+name pair: a workspace, space, folder, or list in the setup hierarchy.</summary>
public sealed record NamedEntity(string Id, string Name);

/// <summary>A selectable status from a list's workflow.</summary>
public sealed record StatusOption(string Name, string? Color);

/// <summary>A unified task as shown in the to-do list, merged from either source endpoint.</summary>
public sealed record TaskItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Url { get; init; }
    public string? StatusName { get; init; }
    public string? StatusColor { get; init; }
    public string? ListId { get; init; }
    public string? ListName { get; init; }

    /// <summary>Due date as Unix epoch milliseconds, or null when undated.</summary>
    public long? DueDateMs { get; init; }

    /// <summary>Last-activity time (ClickUp <c>date_updated</c>) as Unix epoch milliseconds, or null.</summary>
    public long? UpdatedMs { get; init; }
}

/// <summary>A single custom field on a task. Only the field's identity (name/type) is surfaced;
/// the loosely-typed value is intentionally not rendered yet (tracked as a follow-up).</summary>
public sealed record CustomFieldItem(string Name, string? Type);

/// <summary>A comment on a task, as shown in the detail view's Comments tab.</summary>
public sealed record CommentItem(string Id, string Author, long? DateMs, string Text, bool Resolved);

/// <summary>
/// The full detail of a single task, fetched on demand for the detail view (issue #17). Richer than
/// <see cref="TaskItem"/>: it carries the description, tags, assignees, dates, priority and custom
/// fields. Shaped to also seed the agent-dispatch prompt composer (#24).
/// </summary>
public sealed record TaskDetail
{
    public required string Id { get; init; }
    public string? CustomId { get; init; }
    public required string Name { get; init; }
    public string? Url { get; init; }
    public string? StatusName { get; init; }
    public string? StatusColor { get; init; }
    public string? ListId { get; init; }
    public string? ListName { get; init; }

    /// <summary>Plain-text description (ClickUp <c>text_content</c>, falling back to <c>description</c>).</summary>
    public string? Description { get; init; }
    public string? Priority { get; init; }

    public long? DueDateMs { get; init; }
    public long? CreatedMs { get; init; }
    public long? UpdatedMs { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Assignees { get; init; } = [];
    public IReadOnlyList<CustomFieldItem> CustomFields { get; init; } = [];
}
