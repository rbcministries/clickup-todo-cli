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

    /// <summary>
    /// Additional list membership from ClickUp's "Tasks in Multiple Lists" feature (the task
    /// response's <c>locations</c>), distinct from the home <see cref="ListName"/>. Empty for the
    /// common single-list case.
    /// </summary>
    public IReadOnlyList<NamedEntity> Lists { get; init; } = [];

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
