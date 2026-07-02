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

    /// <summary>Priority importance (ordinal: Urgent → High → Normal → Low).</summary>
    Priority,

    /// <summary>
    /// Task assignee(s). Multi-valued (a task has zero or many). An <c>IS</c> rule scopes the
    /// server-side task fetch (#68); grouping/sorting use the first assignee.
    /// </summary>
    Assignee,
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

    /// <summary>
    /// When true, subtasks are shown nested (indented) directly beneath their parent (F4, #46). When
    /// false (the default) subtasks are hidden from the main list so it stays a flat top-level view.
    /// </summary>
    public bool ShowSubtasks { get; set; }

    /// <summary>
    /// The literal filter value that means "the current app user" for an <see cref="TaskField.Assignee"/>
    /// rule. Kept as a token (not a numeric id) so the seeded default doesn't need the user id at
    /// config-load time; it's resolved to the id only at the fetch layer (#68).
    /// </summary>
    public const string CurrentUserToken = "me";

    /// <summary>The default view's single rule — <c>Assignee IS me</c> — which reproduces the app's
    /// original "my tasks" fetch now that assignee is a first-class filter field (#68).</summary>
    public static FilterRule DefaultAssigneeRule() =>
        new() { Field = TaskField.Assignee, Op = FilterOp.Is, Value = CurrentUserToken };

    /// <summary>
    /// True when the view is exactly the seeded default — the single <c>Assignee IS me</c> rule and
    /// nothing else. (An "untouched install" is no longer zero filters; it's the one default assignee
    /// rule, #68.)
    /// </summary>
    public bool IsDefault =>
        Filters.Count == 1 && IsDefaultAssigneeRule(Filters[0])
        && SortField is null && GroupField is null && !ShowSubtasks;

    private static bool IsDefaultAssigneeRule(FilterRule r) =>
        r.Field == TaskField.Assignee && r.Op == FilterOp.Is
        && string.Equals(r.Value, CurrentUserToken, StringComparison.OrdinalIgnoreCase);
}
