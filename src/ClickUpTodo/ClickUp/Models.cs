using System.Globalization;
using System.Text.Json;

namespace ClickUpTodo.ClickUp;

/// <summary>
/// Canonical ClickUp priority mapping. ClickUp exposes four fixed priorities whose importance is
/// ordinal: <c>id</c> "1".."4" (and matching names) run Urgent → High → Normal → Low, where a
/// <b>lower level number means more urgent</b>. Centralised here so the mapper and the F3 engine share
/// one source of truth (kept in the domain layer to avoid a dependency from the client onto Services).
/// </summary>
public static class ClickUpPriority
{
    /// <summary>Canonical priority names, most urgent first.</summary>
    public static readonly IReadOnlyList<string> Names = ["Urgent", "High", "Normal", "Low"];

    /// <summary>Priority name → importance level (1=Urgent … 4=Low), or null when unrecognised.</summary>
    public static int? LevelFromName(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "urgent" => 1,
        "high" => 2,
        "normal" => 3,
        "low" => 4,
        _ => null,
    };

    /// <summary>Importance level → canonical priority name, or null for an out-of-range level.</summary>
    public static string? NameFromLevel(int? level) => level switch
    {
        1 => "Urgent",
        2 => "High",
        3 => "Normal",
        4 => "Low",
        _ => null,
    };

    /// <summary>
    /// The canonical ClickUp priority colour (hex) for an importance <paramref name="level"/>
    /// (1=Urgent…4=Low), or null when the level is unset/out-of-range. These are ClickUp's fixed
    /// per-priority colours; centralised here as the single source of truth so an optimistic priority
    /// change can show the right badge colour without a read-back, and the group-header palette can
    /// fall back to them (see <c>GroupHeaderPalette</c>).
    /// </summary>
    public static string? ColorFromLevel(int? level) => level switch
    {
        1 => "#f50000", // Urgent — red
        2 => "#ffcc00", // High — yellow
        3 => "#6fddff", // Normal — light blue
        4 => "#d8d8d8", // Low — gray
        _ => null,
    };

    /// <summary>
    /// Derives the importance level from a ClickUp priority object's <c>id</c> (the canonical "1".."4"
    /// string), falling back to the priority name when the id is absent/unexpected. Null when neither
    /// yields a level (no priority set, or an unrecognised custom priority).
    /// </summary>
    public static int? Level(string? id, string? name)
    {
        if (TryLevelString(id, out var level))
            return level;
        return LevelFromName(name);
    }

    /// <summary>
    /// Parses a user-entered priority filter value — either a name ("urgent") or a level string
    /// ("1".."4") — to an importance level, or null when it is neither (e.g. "(none)" or a typo, which
    /// callers treat as the no-priority bucket).
    /// </summary>
    public static int? LevelFromFilterValue(string? value)
        => LevelFromName(value) ?? (TryLevelString(value, out var level) ? level : null);

    private static bool TryLevelString(string? value, out int level)
        => int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out level)
            && level is >= 1 and <= 4;
}

// Stable domain records the rest of the app consumes. The Kiota-generated client produces a
// different response type per endpoint (the spec uses inline schemas), so ClickUpClient maps all
// of them into these few shapes — insulating the TUI from regeneration churn.

/// <summary>The signed-in ClickUp user.</summary>
public sealed record ClickUpUser(long Id, string DisplayName);

/// <summary>A member of a Workspace: the numeric ClickUp id plus the username/email a user can type in
/// an <c>Assignee IS</c> filter, so a typed name/email resolves to an id for the server-side fetch (#73).</summary>
public sealed record WorkspaceMember(long Id, string? Username, string? Email);

/// <summary>An id+name pair: a workspace, space, folder, or list in the setup hierarchy.</summary>
public sealed record NamedEntity(string Id, string Name);

/// <summary>A selectable status from a list's workflow.</summary>
public sealed record StatusOption(string Name, string? Color);

/// <summary>A user assigned to a task: the numeric ClickUp id (for stable matching / the app user) and
/// a display name (for labels and grouping).</summary>
public sealed record TaskAssignee(long Id, string Name);

/// <summary>
/// The fields for creating a task via <see cref="IClickUpClient.CreateTaskAsync"/> (#209) — the stable,
/// domain-facing input to the create-task facade, so callers (the New Task screen, #213/#215) never touch
/// the generated request type. Only <see cref="Name"/> is required; the rest are omitted from the request
/// when unset (null / empty assignees). Shaped forward-compatibly so the later Tags epic can add tags
/// without reshaping. <see cref="PriorityLevel"/> is ClickUp's importance level (1=Urgent … 4=Low; see
/// <see cref="ClickUpPriority"/>); <see cref="DueDateMs"/> is Unix epoch milliseconds.
/// </summary>
public sealed record NewTaskRequest
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<long> Assignees { get; init; } = [];
    public int? PriorityLevel { get; init; }
    public long? DueDateMs { get; init; }
}

/// <summary>A unified task as shown in the to-do list, merged from either source endpoint.</summary>
public sealed record TaskItem
{
    public required string Id { get; init; }

    /// <summary>
    /// The Space-defined custom id (ClickUp <c>custom_id</c>, e.g. <c>ABC-123</c>) when the task's Space
    /// has custom ids enabled, else null. Surfaced on the row as a leading identifier chip beside the
    /// Status/Priority badges, falling back to <see cref="Id"/> when unset. Custom-id formats vary by
    /// Space, so its display width is nonstandard.
    /// </summary>
    public string? CustomId { get; init; }

    public required string Name { get; init; }
    public string? Url { get; init; }
    public string? StatusName { get; init; }
    public string? StatusColor { get; init; }

    /// <summary>
    /// The workflow category of the status (ClickUp <c>status.type</c>: <c>open</c>, <c>custom</c>,
    /// <c>done</c>, or <c>closed</c>), or null when the API omits it. <c>closed</c> is ClickUp's terminal
    /// closed type — exactly what a task fetch with <c>include_closed=false</c> drops server-side. Used by
    /// the delta refresh (#194) to recognise a task that closed since the last snapshot (the delta fetch
    /// includes closed tasks precisely so the merge can drop them) and by the F12 "Show Completed" toggle
    /// (#178) to hide completed tasks/subtasks consistently at every level (see <c>TaskView.IsCompleted</c>).
    /// </summary>
    public string? StatusType { get; init; }

    public string? ListId { get; init; }
    public string? ListName { get; init; }

    /// <summary>
    /// The id of this task's parent task (ClickUp <c>parent</c>) when it's a subtask, else null. Used
    /// by the F4 subtasks view (#46) to nest a subtask beneath its parent.
    /// </summary>
    public string? ParentId { get; init; }

    /// <summary>Due date as Unix epoch milliseconds, or null when undated.</summary>
    public long? DueDateMs { get; init; }

    /// <summary>Creation time (ClickUp <c>date_created</c>) as Unix epoch milliseconds, or null.</summary>
    public long? CreatedMs { get; init; }

    /// <summary>Last-activity time (ClickUp <c>date_updated</c>) as Unix epoch milliseconds, or null.</summary>
    public long? UpdatedMs { get; init; }

    /// <summary>Priority importance level: 1=Urgent, 2=High, 3=Normal, 4=Low (lower = more urgent), or null when unset.</summary>
    public int? PriorityLevel { get; init; }

    /// <summary>Canonical priority name ("Urgent"/"High"/"Normal"/"Low"), or null when unset.</summary>
    public string? PriorityName { get; init; }

    /// <summary>Priority hex colour (ClickUp <c>priority.color</c>, e.g. <c>#f50000</c>), or null when
    /// unset. Rendered as the priority badge's background, mirroring <see cref="StatusColor"/>.</summary>
    public string? PriorityColor { get; init; }

    /// <summary>
    /// The task's assignees (ClickUp <c>assignees</c>), empty when unassigned. Carried on the list item
    /// (not just <see cref="TaskDetail"/>) so the F3 view can filter/sort/group by assignee (#68).
    /// </summary>
    public IReadOnlyList<TaskAssignee> Assignees { get; init; } = [];
}

/// <summary>One selectable option of a drop-down or labels custom field. Drop-down options carry a
/// <see cref="Name"/>; labels options carry a label (mapped into <see cref="Name"/> too). A task's
/// value references an option by <see cref="Id"/> or (for older drop-downs) by <see cref="OrderIndex"/>.</summary>
public sealed record CustomFieldOption(string? Id, string? Name, double? OrderIndex);

/// <summary>A single custom field on a task. <see cref="Name"/>/<see cref="Type"/> are the stable
/// identity; <see cref="Value"/> is the loosely-typed value (varies by field type) surfaced as a
/// neutral <see cref="JsonElement"/>, and <see cref="Options"/> are the drop-down/label option
/// definitions used to map a selected id/orderindex to its label. Interpreting the value per type is
/// the (pure, testable) job of <c>TaskDetailFormatter.CustomFieldValue</c>.</summary>
public sealed record CustomFieldItem(
    string Name,
    string? Type,
    JsonElement? Value = null,
    IReadOnlyList<CustomFieldOption>? Options = null)
{
    /// <summary>The field's options, never null (empty when the field has none).</summary>
    public IReadOnlyList<CustomFieldOption> Options { get; init; } = Options ?? [];
}

/// <summary>
/// A comment on a task, as shown in the detail view's Comments tab and — aggregated across many
/// tasks — in the mentions/comments feed (#109). <see cref="TaskId"/> attributes the comment to the
/// task it belongs to so the feed can group it and open that task from a feed entry (#111 / #115); it
/// is null for callers (like the single-task detail view) that don't need attribution.
/// <see cref="MentionsMe"/> is stamped by the feed (#113) when the comment mentions the current user;
/// it defaults to <c>false</c> so the mapper and non-feed callers are unaffected.
/// <see cref="MentionedUserIds"/> carries the numeric ids of members @-mentioned in the comment's
/// structured blocks (#167), enabling id-based mention detection alongside the <c>@handle</c> text match.
/// </summary>
public sealed record CommentItem(
    string Id, string Author, long? DateMs, string Text, bool Resolved, string? TaskId = null,
    bool MentionsMe = false, IReadOnlyList<long>? MentionedUserIds = null)
{
    /// <summary>The numeric ids of members @-mentioned in the comment's structured <c>comment</c> blocks
    /// (#167); never null (empty when the comment mentions no one, or the blocks weren't mapped).
    /// NOTE: as a collection member it participates in the record's synthesized equality by <b>reference</b>,
    /// so two content-equal <see cref="CommentItem"/>s from separate mappings are not <c>Equals</c>. No
    /// consumer relies on <see cref="CommentItem"/> value equality (the feed de-dups by <see cref="Id"/>);
    /// give this member structural equality before using it as a <c>HashSet</c>/dictionary key.</summary>
    public IReadOnlyList<long> MentionedUserIds { get; init; } = MentionedUserIds ?? [];
}

/// <summary>
/// A "recent activity" feed entry (#117): a recently-updated assigned task, surfaced alongside the
/// comment feed (#109) when the feed's <c>F6</c> "show activity" display state is on. Approximates
/// per-task activity via ClickUp <c>date_updated</c> (<see cref="UpdatedMs"/>) — ClickUp has no
/// task-activity-history endpoint — projected from the assigned tasks the feed already fetches, so it
/// needs no new API surface. <see cref="Id"/> is prefixed (<c>"activity:" + TaskId</c>) so it can never
/// collide with a <see cref="CommentItem.Id"/> in the merged feed's de-dup / selection tracking, and
/// <see cref="TaskId"/> lets a feed row open the task exactly like a comment row (#115).
/// </summary>
public sealed record ActivityItem(
    string Id, string TaskId, string TaskName, string? StatusName, long? UpdatedMs)
{
    /// <summary>The <see cref="Id"/> prefix that namespaces an activity entry apart from comment ids in
    /// the merged feed. A comment id is a bare ClickUp id, so this prefix guarantees disjoint id spaces.</summary>
    public const string IdPrefix = "activity:";

    /// <summary>Projects a recently-updated <see cref="TaskItem"/> into an activity feed entry. The
    /// resulting <see cref="Id"/> is <see cref="IdPrefix"/> + the task id.</summary>
    public static ActivityItem FromTask(TaskItem task) => new(
        IdPrefix + task.Id, task.Id, task.Name, task.StatusName, task.UpdatedMs);
}

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

    /// <summary>
    /// Additional list membership from ClickUp's "Tasks in Multiple Lists" feature (the task
    /// response's <c>locations</c>), distinct from the home <see cref="ListName"/>. Empty for the
    /// common single-list case.
    /// </summary>
    public IReadOnlyList<NamedEntity> Lists { get; init; } = [];

    /// <summary>Plain-text description (ClickUp <c>text_content</c>, falling back to <c>description</c>).</summary>
    public string? Description { get; init; }
    public string? Priority { get; init; }

    /// <summary>Priority hex color for the detail Other tab's coloured <c>Priority:</c> value; null when
    /// unset. Mirrors <see cref="TaskItem.PriorityColor"/> and the sibling <see cref="StatusColor"/>.</summary>
    public string? PriorityColor { get; init; }

    public long? DueDateMs { get; init; }
    public long? CreatedMs { get; init; }
    public long? UpdatedMs { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Assignees { get; init; } = [];
    public IReadOnlyList<CustomFieldItem> CustomFields { get; init; } = [];
}
