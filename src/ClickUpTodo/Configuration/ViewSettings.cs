namespace ClickUpTodo.Configuration;

/// <summary>A task attribute usable for filtering, sorting, and grouping the list (F3).</summary>
public enum TaskField
{
    Status,
    List,

    /// <summary>Creation timestamp (ClickUp <c>date_created</c>), epoch ms.</summary>
    Created,

    /// <summary>Last-activity timestamp (ClickUp <c>date_updated</c>), epoch ms.</summary>
    LastActivity,

    /// <summary>Due date, epoch ms.</summary>
    Due,
}

/// <summary>
/// A filter comparison. <see cref="Is"/>/<see cref="IsNot"/> apply to every field; the ordering
/// operators apply only to numeric/date fields (Created, Last activity, Due).
/// </summary>
public enum FilterOp
{
    Is,
    IsNot,
    GreaterThan,
    LessThan,
    GreaterOrEqual,
    LessOrEqual,
}

/// <summary>Sort direction for the active sort field.</summary>
public enum SortDirection
{
    Ascending,
    Descending,
}

/// <summary>
/// A single filter rule: <see cref="Field"/> <see cref="Op"/> <see cref="Value"/>. For categorical
/// fields the value is matched case-insensitively; for numeric/date fields it is an epoch-ms value
/// (or a date the dialog has normalized to one).
/// </summary>
public sealed record FilterRule
{
    public TaskField Field { get; init; }
    public FilterOp Op { get; init; }
    public string Value { get; init; } = "";
}

/// <summary>
/// The persisted filter/sort/group view applied to the task list (F3). Persisted in
/// <c>config.json</c> so it survives restarts. An "empty" view (no filters, no sort, no group)
/// reproduces the app's default ordering.
/// </summary>
public sealed class ViewSettings
{
    /// <summary>Filter rules, ANDed together. Empty = no filtering.</summary>
    public List<FilterRule> Filters { get; set; } = [];

    /// <summary>The field to sort by, or null for the default order (due date, then name).</summary>
    public TaskField? SortField { get; set; }

    public SortDirection SortDirection { get; set; } = SortDirection.Ascending;

    /// <summary>The field to group by, or null for a single ungrouped section.</summary>
    public TaskField? GroupField { get; set; }

    /// <summary>True when nothing is configured, so the default order/sectioning applies.</summary>
    public bool IsDefault => Filters.Count == 0 && SortField is null && GroupField is null;
}
