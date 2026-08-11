using ClickUpTodo.Configuration;
using ClickUpTodo.Services;

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// Pure input-handling logic for the F3 filter/sort/group screen, factored out of the Terminal.Gui
/// glue so it can be unit-tested: the field/operator option lists, the mapping between a picker's
/// selected index and a nullable <see cref="TaskField"/>, and validation when building a filter rule.
/// </summary>
public static class FilterSortGroupForm
{
    /// <summary>The filterable/sortable/groupable fields, in display order.</summary>
    public static readonly IReadOnlyList<TaskField> Fields =
        [TaskField.Status, TaskField.List, TaskField.Assignee, TaskField.Created, TaskField.LastActivity, TaskField.Due, TaskField.Priority];

    /// <summary>All operators, in display order (validity per field is enforced by <see cref="TryBuildRule"/>).</summary>
    public static readonly IReadOnlyList<FilterOp> Ops =
        [FilterOp.Is, FilterOp.IsNot, FilterOp.GreaterThan, FilterOp.LessThan, FilterOp.GreaterOrEqual, FilterOp.LessOrEqual];

    /// <summary>The sort/group picker choices: "(none)" first, then each field's display name.</summary>
    public static IReadOnlyList<string> FieldChoices()
        => new[] { "(none)" }.Concat(Fields.Select(TaskFieldInfo.DisplayName)).ToList();

    /// <summary>Picker index for a (nullable) field: 0 = "(none)", else the field's position + 1.</summary>
    public static int FieldToIndex(TaskField? field)
        => field is null ? 0 : Fields.ToList().IndexOf(field.Value) + 1;

    /// <summary>The field for a sort/group picker index (0 → null/"(none)"), tolerating out-of-range.</summary>
    public static TaskField? IndexToField(int? selected)
        => selected is int i && i >= 1 && i <= Fields.Count ? Fields[i - 1] : null;

    /// <summary>Clamps a possibly-null/out-of-range selection to a valid index into a list of <paramref name="count"/>.</summary>
    public static int Clamp(int? selected, int count)
        => selected is int i && i >= 0 && i < count ? i : 0;

    /// <summary>
    /// Builds the saved <see cref="ViewSettings"/> from the form's edited state: the active filter
    /// rules and the sort/group picker indices (0 = "(none)") + direction. The F4 subtasks view (#179)
    /// and F12 completed view (#191) are <b>preserved from <paramref name="current"/></b> — this screen
    /// doesn't edit them, and reconstructing without them would silently reset subtasks to Hidden (and,
    /// since #68, flip <c>IsDefault</c>) and completed to Active on any save. Pure so the marshalling the
    /// Save handler performs — the axis Help's #404 modal never exercised — is unit-testable.
    /// </summary>
    public static ViewSettings BuildResult(
        IReadOnlyList<FilterRule> filters, int? sortIndex, SortDirection direction, int? groupIndex, ViewSettings current)
        => new()
        {
            Filters = filters.ToList(),
            SortField = IndexToField(sortIndex),
            SortDirection = direction,
            GroupField = IndexToField(groupIndex),
            Subtasks = current.Subtasks,
            Completed = current.Completed,
        };

    /// <summary>
    /// Validates and builds a filter rule from the picker selections: the value must be non-blank, and
    /// the operator must be valid for the field (ordering operators only on numeric/date and ordinal
    /// fields; assignee is IS-only, #68). Returns false with an <paramref name="error"/> message otherwise.
    /// </summary>
    public static bool TryBuildRule(TaskField field, FilterOp op, string? value, out FilterRule? rule, out string? error)
    {
        rule = null;
        error = null;
        var trimmed = value?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            error = "Enter a value before adding a filter.";
            return false;
        }
        var validOps = TaskFieldInfo.ValidOps(field);
        if (!validOps.Contains(op))
        {
            error = $"{TaskFieldInfo.DisplayName(field)} only supports {string.Join(" / ", validOps.Select(TaskFieldInfo.OpSymbol))}.";
            return false;
        }
        rule = new FilterRule { Field = field, Op = op, Value = trimmed };
        return true;
    }
}
