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
