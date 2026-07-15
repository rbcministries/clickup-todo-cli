using System.Text.Json.Serialization;

namespace ClickUpTodo.Configuration;

/// <summary>
/// The three-state subtask view cycled by F4 (#179), superseding the old
/// <c>ShowSubtasks</c> + <c>ShowAllSubtasksOfAssignedParents</c> boolean pair. F4 advances
/// <see cref="MineAndUnassigned"/> -> <see cref="All"/> -> <see cref="Hidden"/> -> …
/// (see <see cref="SubtaskViewExtensions.Next"/>).
/// </summary>
public enum SubtaskView
{
    /// <summary>Subtasks are hidden; the main list stays a flat top-level view (the default, and the
    /// pre-#179 <c>ShowSubtasks == false</c> behaviour).</summary>
    Hidden,

    /// <summary>The default on-state: nested subtasks that are assigned <b>to me</b> (already in the
    /// snapshot) or <b>unassigned</b> (pulled in and marked <c>(unassigned)</c>). Subtasks assigned only
    /// to others are excluded.</summary>
    MineAndUnassigned,

    /// <summary>Additionally include subtasks <b>not assigned to me</b> — the pre-#179
    /// <c>ShowAllSubtasksOfAssignedParents</c> (#70) behaviour, marked <c>(not assigned to you)</c>.</summary>
    All,
}

/// <summary>Cycle order and display text for <see cref="SubtaskView"/> (F4, #179). Pure and
/// unit-testable.</summary>
public static class SubtaskViewExtensions
{
    /// <summary>The next state in the F4 cycle: MineAndUnassigned -> All -> Hidden -> MineAndUnassigned,
    /// so pressing F4 from Hidden lands on the default on-state and wraps 1 -> 2 -> 3 -> 1.</summary>
    public static SubtaskView Next(this SubtaskView state) => state switch
    {
        SubtaskView.MineAndUnassigned => SubtaskView.All,
        SubtaskView.All => SubtaskView.Hidden,
        _ => SubtaskView.MineAndUnassigned,
    };

    /// <summary>The transient status-line description shown when F4 lands on <paramref name="state"/>.</summary>
    public static string Describe(this SubtaskView state) => state switch
    {
        SubtaskView.MineAndUnassigned => "Subtasks: mine + unassigned — → expand a parent, ← collapse (F4).",
        SubtaskView.All => "Subtasks: all, including others' (F4).",
        _ => "Subtasks hidden (F4).",
    };

    /// <summary>A compact frame-title flag for the active subtask view, or null when hidden.</summary>
    public static string? TitleFlag(this SubtaskView state) => state switch
    {
        SubtaskView.MineAndUnassigned => "subtasks: mine+unassigned",
        SubtaskView.All => "subtasks: all",
        _ => null,
    };
}

/// <summary>
/// The three-state "Show Completed" view cycled by F12 (#191), superseding the earlier
/// <c>ShowCompleted</c> boolean (#178). F12 advances <see cref="Active"/> -> <see cref="WithDone"/>
/// -> <see cref="All"/> -> … (see <see cref="CompletedViewExtensions.Next"/>). "Completed" is scoped
/// strictly to ClickUp's status <c>type</c> (<c>done</c>/<c>closed</c>), not user-named statuses.
/// </summary>
public enum CompletedView
{
    /// <summary>Only active work: both <c>done</c>-type and <c>closed</c>-type tasks are hidden (the
    /// default, #191). This hides more than the pre-#178 top-level view did — <c>done</c>-type tasks
    /// were previously visible — because the server returns <c>done</c>-type tasks regardless of
    /// <c>include_closed</c>, so the extra hiding is client-side.</summary>
    Active,

    /// <summary>Include <c>done</c>-type tasks; still hide <c>closed</c>-type. Matches the app's
    /// historical top-level default (closed dropped server-side, done shown) — the migration target for
    /// a pre-tri-state <c>ShowCompleted == false</c> config.</summary>
    WithDone,

    /// <summary>Include everything: both <c>done</c>-type and <c>closed</c>-type. The only state whose
    /// fetch must widen to <c>include_closed=true</c> (see <see cref="ViewSettings.IncludesClosedTasks"/>);
    /// the migration target for <c>ShowCompleted == true</c>.</summary>
    All,
}

/// <summary>Cycle order and display text for <see cref="CompletedView"/> (F12, #191). Pure and
/// unit-testable, mirroring <see cref="SubtaskViewExtensions"/>.</summary>
public static class CompletedViewExtensions
{
    /// <summary>The next state in the F12 cycle: Active -> WithDone -> All -> Active, so pressing F12
    /// walks default → + done → + done &amp; closed and wraps.</summary>
    public static CompletedView Next(this CompletedView state) => state switch
    {
        CompletedView.Active => CompletedView.WithDone,
        CompletedView.WithDone => CompletedView.All,
        _ => CompletedView.Active,
    };

    /// <summary>The transient status-line description shown when F12 lands on <paramref name="state"/>.</summary>
    public static string Describe(this CompletedView state) => state switch
    {
        CompletedView.Active => "Completed: active only — done & closed hidden (F12).",
        CompletedView.WithDone => "Completed: showing done (F12).",
        _ => "Completed: showing done & closed (F12).",
    };

    /// <summary>A compact frame-title flag for the active completed view, or null in the default
    /// (<see cref="CompletedView.Active"/>) state.</summary>
    public static string? TitleFlag(this CompletedView state) => state switch
    {
        CompletedView.WithDone => "+done",
        CompletedView.All => "+done & closed",
        _ => null,
    };
}

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
    /// The three-state subtask view cycled by F4 (#179): hidden, mine + unassigned, or all. The single
    /// source of truth, superseding the <see cref="ShowSubtasks"/> / <see cref="ShowAllSubtasksOfAssignedParents"/>
    /// boolean pair (kept below as read-only convenience getters). Persisted by name via the
    /// enum-as-string converter; a legacy boolean config is migrated onto it (see <see cref="ConfigMigrations"/>).
    /// </summary>
    public SubtaskView Subtasks { get; set; } = SubtaskView.Hidden;

    /// <summary>
    /// True when subtasks are nested at all (F4 not in <see cref="SubtaskView.Hidden"/>) — i.e. either
    /// on-state. Read-only convenience over <see cref="Subtasks"/>; not persisted (a legacy
    /// <c>showSubtasks</c> key is read once by the migration via <see cref="LegacyShowSubtasks"/>).
    /// </summary>
    [JsonIgnore]
    public bool ShowSubtasks => Subtasks != SubtaskView.Hidden;

    /// <summary>
    /// True only in <see cref="SubtaskView.All"/> — subtasks not assigned to me are pulled in and nested
    /// as not-mine context rows (#70). Read-only convenience over <see cref="Subtasks"/>; not persisted.
    /// </summary>
    [JsonIgnore]
    public bool ShowAllSubtasksOfAssignedParents => Subtasks == SubtaskView.All;

    /// <summary>Legacy boolean subtask setting (pre-#179), retained only as a <b>deserialize-only</b>
    /// migration shim: <see cref="ConfigMigrations"/> reads a saved <c>showSubtasks</c> on load, folds it
    /// into <see cref="Subtasks"/>, then nulls it so it's never written again.</summary>
    [JsonPropertyName("showSubtasks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyShowSubtasks { get; set; }

    /// <summary>Legacy #70 boolean (pre-#179), retained only as a <b>deserialize-only</b> migration shim,
    /// like <see cref="LegacyShowSubtasks"/>.</summary>
    [JsonPropertyName("showAllSubtasksOfAssignedParents")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyShowAllSubtasks { get; set; }

    /// <summary>
    /// The three-state "Show Completed" view cycled by F12 (#191): <see cref="CompletedView.Active"/>
    /// (hide done + closed — the default), <see cref="CompletedView.WithDone"/> (show done, hide
    /// closed), or <see cref="CompletedView.All"/> (show everything). The single source of truth,
    /// superseding the pre-#178 <c>ShowCompleted</c> boolean (kept below as a deserialize-only migration
    /// shim). Scoped strictly to ClickUp's status <c>type</c>, so it composes with — rather than
    /// duplicating — the F3 <c>Status IS NOT</c> filters, and applies consistently to top-level tasks
    /// and pulled-in subtasks. Persisted by name via the enum-as-string converter.
    /// </summary>
    public CompletedView Completed { get; set; } = CompletedView.Active;

    /// <summary>
    /// True only in <see cref="CompletedView.All"/> — the one state whose fetch must widen to
    /// <c>include_closed=true</c>. <c>done</c>-type tasks are returned regardless of that flag, so the
    /// Active↔WithDone difference is a pure client-side re-filter with no fetch impact; only reaching
    /// <see cref="CompletedView.All"/> needs the server to return <c>closed</c>-type tasks. Drives both
    /// the full-load fetch flag and the delta merge's keep-closed (#191). Read-only; not persisted.
    /// </summary>
    [JsonIgnore]
    public bool IncludesClosedTasks => Completed == CompletedView.All;

    /// <summary>Legacy boolean "Show Completed" setting (#178, pre-#191 tri-state), retained only as a
    /// <b>deserialize-only</b> migration shim (like <see cref="LegacyShowSubtasks"/>):
    /// <see cref="ConfigMigrations"/> reads a saved <c>showCompleted</c> on load, folds it into
    /// <see cref="Completed"/> (<c>false</c> → <see cref="CompletedView.WithDone"/> to preserve the
    /// historical done-visible view, <c>true</c> → <see cref="CompletedView.All"/>), then nulls it so
    /// it's never written again.</summary>
    [JsonPropertyName("showCompleted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyShowCompleted { get; set; }

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
    /// The statuses hidden by default. These used to live in <c>AppConfig.ExcludedStatuses</c>; they're
    /// now seeded as <c>Status IS NOT</c> rules so visibility is decided solely by the F3 filter engine
    /// (#69). A fresh install seeds one rule per entry; existing users' saved exclusions are migrated in
    /// their place (see <see cref="ConfigMigrations"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultExcludedStatuses = ["won't do", "cancelled"];

    /// <summary>A <c>Status IS NOT <paramref name="status"/></c> rule — the filter-engine equivalent of
    /// a legacy excluded status (#69).</summary>
    public static FilterRule StatusIsNotRule(string status) =>
        new() { Field = TaskField.Status, Op = FilterOp.IsNot, Value = status };

    /// <summary>
    /// True when the view is exactly the seeded default and nothing else: the single
    /// <c>Assignee IS me</c> rule (#68) plus one <c>Status IS NOT</c> rule for each
    /// <see cref="DefaultExcludedStatuses"/> entry (#69), with no sort, group, or subtasks. Order of
    /// the filters doesn't matter. (An "untouched install" is no longer zero filters — nor just the
    /// assignee rule — it's the assignee rule plus the default status exclusions.)
    /// </summary>
    public bool IsDefault
    {
        get
        {
            if (SortField is not null || GroupField is not null || Subtasks != SubtaskView.Hidden
                || Completed != CompletedView.Active)
                return false;
            if (Filters.Count != 1 + DefaultExcludedStatuses.Count)
                return false;
            if (Filters.Count(IsDefaultAssigneeRule) != 1)
                return false;
            // Every default exclusion must be present; with the exact count above, that leaves no room
            // for extra or duplicate rules.
            return DefaultExcludedStatuses.All(s => Filters.Any(r => IsStatusIsNotRule(r, s)));
        }
    }

    private static bool IsDefaultAssigneeRule(FilterRule r) =>
        r.Field == TaskField.Assignee && r.Op == FilterOp.Is
        && string.Equals(r.Value, CurrentUserToken, StringComparison.OrdinalIgnoreCase);

    private static bool IsStatusIsNotRule(FilterRule r, string status) =>
        r.Field == TaskField.Status && r.Op == FilterOp.IsNot
        && string.Equals(r.Value, status, StringComparison.OrdinalIgnoreCase);
}
