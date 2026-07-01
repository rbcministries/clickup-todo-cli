using System.Globalization;
using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Services;

/// <summary>A labelled group of tasks for grouped rendering. <see cref="Label"/> is null when ungrouped.</summary>
public sealed record TaskGroup(string? Label, IReadOnlyList<TaskItem> Tasks);

/// <summary>
/// Metadata and value accessors for the F3 filter/sort/group <see cref="TaskField"/>s. Pure and
/// unit-tested; keeps the dialog and the <see cref="TaskView"/> engine free of per-field switches.
/// </summary>
public static class TaskFieldInfo
{
    /// <summary>Numeric/date fields support the ordering operators; categorical fields do not.</summary>
    public static bool IsNumeric(TaskField field) => field is TaskField.LastActivity or TaskField.Due;

    /// <summary>
    /// Ordinal fields (Priority) carry an ordered set of named levels. Like numeric fields they support
    /// the ordering operators, but they sort/group by level (not alphabetically) and render labels by
    /// name (not <c>yyyy-MM-dd</c>) — so the engine treats them as a distinct third kind.
    /// </summary>
    public static bool IsOrdinal(TaskField field) => field is TaskField.Priority;

    public static string DisplayName(TaskField field) => field switch
    {
        TaskField.Status => "Status",
        TaskField.List => "List",
        TaskField.LastActivity => "Last activity",
        TaskField.Due => "Due date",
        TaskField.Priority => "Priority",
        _ => field.ToString(),
    };

    /// <summary>Operators valid for a field: all six for numeric/date and ordinal, IS / IS NOT for categorical.</summary>
    public static IReadOnlyList<FilterOp> ValidOps(TaskField field) => IsNumeric(field) || IsOrdinal(field)
        ? [FilterOp.Is, FilterOp.IsNot, FilterOp.GreaterThan, FilterOp.LessThan, FilterOp.GreaterOrEqual, FilterOp.LessOrEqual]
        : [FilterOp.Is, FilterOp.IsNot];

    public static string OpSymbol(FilterOp op) => op switch
    {
        FilterOp.Is => "IS",
        FilterOp.IsNot => "IS NOT",
        FilterOp.GreaterThan => ">",
        FilterOp.LessThan => "<",
        FilterOp.GreaterOrEqual => "GEQ",
        FilterOp.LessOrEqual => "LEQ",
        _ => op.ToString(),
    };

    /// <summary>A human-readable rendering of a rule, e.g. <c>Status IS Done</c>.</summary>
    public static string Describe(FilterRule rule) => $"{DisplayName(rule.Field)} {OpSymbol(rule.Op)} {rule.Value}";

    /// <summary>The categorical (string) value of a field, or null for numeric fields / missing data.</summary>
    public static string? CategoricalValue(TaskItem task, TaskField field) => field switch
    {
        TaskField.Status => task.StatusName,
        TaskField.List => task.ListName,
        _ => null,
    };

    /// <summary>The numeric (epoch-ms) value of a field, or null for categorical fields / missing data.</summary>
    public static long? NumericValue(TaskItem task, TaskField field) => field switch
    {
        TaskField.LastActivity => task.UpdatedMs,
        TaskField.Due => task.DueDateMs,
        _ => null,
    };

    /// <summary>The ordinal importance level of a field (1=Urgent … 4=Low), or null for non-ordinal fields / no priority.</summary>
    public static int? OrdinalValue(TaskItem task, TaskField field) => field switch
    {
        TaskField.Priority => task.PriorityLevel,
        _ => null,
    };

    /// <summary>
    /// Parses a user-entered value for a numeric/date field into epoch milliseconds. Accepts raw
    /// epoch ms, a date (<c>yyyy-MM-dd</c>, interpreted as UTC midnight), or an ISO date-time.
    /// </summary>
    public static bool TryParseNumeric(string? value, out long ms)
    {
        ms = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        value = value.Trim();

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ms))
            return true;

        if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            ms = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            return true;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
        {
            ms = dto.ToUnixTimeMilliseconds();
            return true;
        }

        return false;
    }
}

/// <summary>
/// Pure filter / sort / group engine for the task list (F3). Separated from the TUI so the
/// presentation (a modal today, a screen after #38) can change without touching this logic.
/// </summary>
public static class TaskView
{
    /// <summary>Filters, sorts, then groups <paramref name="tasks"/> per <paramref name="settings"/>.</summary>
    public static IReadOnlyList<TaskGroup> Apply(IEnumerable<TaskItem> tasks, ViewSettings settings)
    {
        var filtered = Filter(tasks, settings.Filters);
        var sorted = Sort(filtered, settings.SortField, settings.SortDirection);
        return Group(sorted, settings.GroupField);
    }

    /// <summary>Keeps only tasks matching every rule (rules are ANDed). Empty/unparseable rules pass.</summary>
    public static IReadOnlyList<TaskItem> Filter(IEnumerable<TaskItem> tasks, IReadOnlyList<FilterRule>? rules)
    {
        if (rules is null || rules.Count == 0)
            return tasks.ToList();
        return tasks.Where(t => rules.All(r => Matches(t, r))).ToList();
    }

    private static bool Matches(TaskItem task, FilterRule rule)
    {
        if (TaskFieldInfo.IsOrdinal(rule.Field))
            return MatchesOrdinal(task, rule);

        if (TaskFieldInfo.IsNumeric(rule.Field))
        {
            // An unparseable target shouldn't silently hide everything — treat it as no-op.
            if (!TaskFieldInfo.TryParseNumeric(rule.Value, out var target))
                return true;
            var value = TaskFieldInfo.NumericValue(task, rule.Field);
            return rule.Op switch
            {
                FilterOp.Is => value == target,
                FilterOp.IsNot => value != target,
                FilterOp.GreaterThan => value is { } v && v > target,
                FilterOp.LessThan => value is { } v && v < target,
                FilterOp.GreaterOrEqual => value is { } v && v >= target,
                FilterOp.LessOrEqual => value is { } v && v <= target,
                _ => true,
            };
        }

        var actual = TaskFieldInfo.CategoricalValue(task, rule.Field);
        var equal = string.Equals(actual ?? "", rule.Value ?? "", StringComparison.OrdinalIgnoreCase);
        return rule.Op switch
        {
            FilterOp.Is => equal,
            FilterOp.IsNot => !equal,
            _ => true, // ordering operators are invalid on categorical fields → ignore
        };
    }

    /// <summary>
    /// Matches an ordinal (priority) rule. IS / IS NOT compare priority by name — an unrecognised
    /// value (including "(none)") matches the no-priority bucket. Ordering operators compare by
    /// <em>importance</em>: "higher priority than X" means more urgent, i.e. a smaller level number,
    /// so <c>&gt; Normal</c> keeps Urgent/High. No-priority tasks never satisfy an ordering op, and an
    /// unparseable ordering target is a no-op (mirroring the numeric path).
    /// </summary>
    private static bool MatchesOrdinal(TaskItem task, FilterRule rule)
    {
        var level = TaskFieldInfo.OrdinalValue(task, rule.Field);
        var target = ClickUpPriority.LevelFromName(rule.Value);

        if (rule.Op is FilterOp.Is or FilterOp.IsNot)
        {
            var equal = level == target; // both null (task has no priority, value is "(none)") counts as equal
            return rule.Op == FilterOp.Is ? equal : !equal;
        }

        if (target is null || level is null)
            return target is null; // unparseable target → no-op; no-priority task → never matches an ordering op
        return rule.Op switch
        {
            FilterOp.GreaterThan => level < target,
            FilterOp.LessThan => level > target,
            FilterOp.GreaterOrEqual => level <= target,
            FilterOp.LessOrEqual => level >= target,
            _ => true,
        };
    }

    /// <summary>
    /// Stable sort. A null <paramref name="field"/> reproduces the default order (due date soonest
    /// first, undated last, then name). Missing values always sort last, regardless of direction.
    /// </summary>
    public static IReadOnlyList<TaskItem> Sort(IEnumerable<TaskItem> tasks, TaskField? field, SortDirection direction)
    {
        var list = tasks.ToList();
        var effectiveField = field ?? TaskField.Due;
        var effectiveDir = field is null ? SortDirection.Ascending : direction;
        // List.Sort is not stable, so fold a deterministic name/id tie-break into the comparison.
        list.Sort((x, y) => Compare(x, y, effectiveField, effectiveDir));
        return list;
    }

    private static int Compare(TaskItem x, TaskItem y, TaskField field, SortDirection direction)
    {
        int primary;
        if (TaskFieldInfo.IsOrdinal(field))
        {
            // Sort by importance level: ascending = Urgent (level 1) first → Low; no-priority last.
            var vx = TaskFieldInfo.OrdinalValue(x, field);
            var vy = TaskFieldInfo.OrdinalValue(y, field);
            primary = CompareNullableLast(vx.HasValue, vx ?? 0, vy.HasValue, vy ?? 0, direction,
                static (a, b) => a.CompareTo(b));
        }
        else if (TaskFieldInfo.IsNumeric(field))
        {
            var vx = TaskFieldInfo.NumericValue(x, field);
            var vy = TaskFieldInfo.NumericValue(y, field);
            primary = CompareNullableLast(vx.HasValue, vx ?? 0, vy.HasValue, vy ?? 0, direction,
                static (a, b) => a.CompareTo(b));
        }
        else
        {
            var sx = TaskFieldInfo.CategoricalValue(x, field);
            var sy = TaskFieldInfo.CategoricalValue(y, field);
            primary = CompareNullableLast(
                !string.IsNullOrWhiteSpace(sx), sx ?? "",
                !string.IsNullOrWhiteSpace(sy), sy ?? "",
                direction, static (a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
        }
        if (primary != 0)
            return primary;

        var byName = string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        return byName != 0 ? byName : string.CompareOrdinal(x.Id, y.Id);
    }

    private static int CompareNullableLast<T>(bool hasA, T a, bool hasB, T b, SortDirection direction, Comparison<T> compare)
    {
        if (!hasA && !hasB)
            return 0;
        if (!hasA)
            return 1; // missing values always last
        if (!hasB)
            return -1;
        var c = compare(a, b);
        return direction == SortDirection.Descending ? -c : c;
    }

    /// <summary>
    /// Partitions an already-sorted list into groups by <paramref name="field"/>, preserving
    /// within-group order. Categorical groups are ordered alphabetically; date groups by calendar
    /// day (UTC); ordinal (priority) groups by importance (Urgent first). The missing-value bucket
    /// (<c>(none)</c> / <c>No date</c>) is always last. A null field yields a single ungrouped
    /// <see cref="TaskGroup"/> (null label).
    /// </summary>
    public static IReadOnlyList<TaskGroup> Group(IReadOnlyList<TaskItem> sortedTasks, TaskField? field)
    {
        if (field is null)
            return [new TaskGroup(null, sortedTasks)];

        var f = field.Value;
        var missingLabel = TaskFieldInfo.IsNumeric(f) ? "No date" : "(none)";
        var buckets = new Dictionary<string, List<TaskItem>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var task in sortedTasks)
        {
            var label = LabelFor(task, f, missingLabel);
            if (!buckets.TryGetValue(label, out var bucket))
            {
                bucket = [];
                buckets[label] = bucket;
                order.Add(label);
            }
            bucket.Add(task);
        }

        var ordered = order.OrderBy(k => string.Equals(k, missingLabel, StringComparison.OrdinalIgnoreCase)); // missing last
        // Ordinal groups order by importance level (Urgent → Low); others alpha (dates sort chronologically as yyyy-MM-dd).
        var byField = TaskFieldInfo.IsOrdinal(f)
            ? ordered.ThenBy(k => ClickUpPriority.LevelFromName(k) ?? int.MaxValue)
            : ordered.ThenBy(k => k, StringComparer.OrdinalIgnoreCase);

        return byField
            .Select(k => new TaskGroup(k, (IReadOnlyList<TaskItem>)buckets[k]))
            .ToList();
    }

    private static string LabelFor(TaskItem task, TaskField field, string missingLabel)
    {
        if (TaskFieldInfo.IsOrdinal(field))
            return ClickUpPriority.NameFromLevel(TaskFieldInfo.OrdinalValue(task, field)) ?? missingLabel;

        if (TaskFieldInfo.IsNumeric(field))
        {
            var v = TaskFieldInfo.NumericValue(task, field);
            return v is { } ms
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : missingLabel;
        }
        var s = TaskFieldInfo.CategoricalValue(task, field);
        return string.IsNullOrWhiteSpace(s) ? missingLabel : s;
    }
}
