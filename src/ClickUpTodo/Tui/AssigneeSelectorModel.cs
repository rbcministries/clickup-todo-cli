using System.Globalization;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui;

/// <summary>The decision for toggling one candidate assignee: what should happen, and to whom.</summary>
public readonly record struct ToggleResult(ToggleKind Kind, long Id);

/// <summary>One rendered row of the assignee selector: the person, whether they're currently a
/// selected assignee (shown with a <c>✓</c>), and whether that selection is locked (a default that
/// can't be removed, e.g. the current user on a New Task).</summary>
public readonly record struct AssigneeRow(long Id, string Name, bool Selected, bool Locked);

/// <summary>
/// The assignee-typed façade over the generic <see cref="SelectorModel"/> (#243). Keeps the assignee
/// boundary in <c>long</c> ids / <see cref="TaskAssignee"/> — adapting to the base's string-id
/// <see cref="SelectorItem"/> here — so the New Task (#213) and Quick Updates (#158) call sites and
/// their tests stay in assignee terms while the pure selection logic lives in one shared place. Assignee
/// ids are always positive; a non-positive id is dropped at this boundary (the base only guards against
/// blank ids / names).
/// </summary>
public static class AssigneeSelectorModel
{
    /// <summary>The display text for a row: a leading <c>✓</c> when selected, else a two-column blank
    /// indent. Delegates to <see cref="SelectorModel.Format"/> (assignees carry no distinguished
    /// marker).</summary>
    public static string Format(AssigneeRow row) => SelectorModel.Format(ToRow(row));

    /// <summary>The empty-search rows: current assignees (marked <c>✓</c>, <c>Locked</c> when in
    /// <paramref name="lockedIds"/>) first, then the most-frequent <paramref name="topFrequent"/>
    /// top-up up to <paramref name="capacity"/>. See <see cref="SelectorModel.EmptyStateRows"/>.</summary>
    public static IReadOnlyList<AssigneeRow> EmptyStateRows(
        IReadOnlyList<TaskAssignee> selected,
        ISet<long> lockedIds,
        IReadOnlyList<TaskAssignee> topFrequent,
        int capacity)
        => SelectorModel.EmptyStateRows(
                ToItems(selected), ToStringSet(lockedIds), EmptyStringSet, ToItems(topFrequent), capacity)
            .Select(ToAssigneeRow)
            .ToList();

    /// <summary>The type-ahead rows: <paramref name="matches"/> as unselected rows, excluding anyone in
    /// <paramref name="selectedIds"/>. See <see cref="SelectorModel.SearchResultRows"/>.</summary>
    public static IReadOnlyList<AssigneeRow> SearchResultRows(
        IReadOnlyList<TaskAssignee> matches, ISet<long> selectedIds)
        => SelectorModel.SearchResultRows(ToItems(matches), ToStringSet(selectedIds))
            .Select(ToAssigneeRow)
            .ToList();

    /// <summary>The add/remove/locked decision for picking assignee <paramref name="id"/>. See
    /// <see cref="SelectorModel.Toggle"/>. The passed sets are not modified.</summary>
    public static ToggleResult Toggle(ISet<long> selectedIds, ISet<long> lockedIds, long id)
    {
        var decision = SelectorModel.Toggle(ToStringSet(selectedIds), ToStringSet(lockedIds), Str(id));
        return new ToggleResult(decision.Kind, id);
    }

    /// <summary>Whether a debounce timer captured at <paramref name="capturedStamp"/> still represents
    /// the latest keystroke (<paramref name="currentStamp"/>). See
    /// <see cref="SelectorModel.ShouldRunSearch"/>.</summary>
    public static bool ShouldRunSearch(long capturedStamp, long currentStamp)
        => SelectorModel.ShouldRunSearch(capturedStamp, currentStamp);

    // ── assignee ↔ base conversions ──────────────────────────────────────────

    private static readonly ISet<string> EmptyStringSet = new HashSet<string>(StringComparer.Ordinal);

    private static string Str(long id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>A <see cref="TaskAssignee"/> as a base <see cref="SelectorItem"/> (string id). Also used
    /// by <see cref="AssigneeSelectorView"/> to adapt its callbacks onto the base.</summary>
    internal static SelectorItem ToItem(TaskAssignee person) => new(Str(person.Id), person.Name);

    /// <summary>A base <see cref="SelectorItem"/> back to a <see cref="TaskAssignee"/> (parsing the
    /// string id). Round-trips losslessly for the positive ids assignees always carry.</summary>
    internal static TaskAssignee ToAssignee(SelectorItem item)
        => new(long.Parse(item.Id, CultureInfo.InvariantCulture), item.Name);

    /// <summary>A set of assignee ids as the base's string-id set.</summary>
    internal static ISet<string> ToStringSet(ISet<long> ids)
        => new HashSet<string>(ids.Select(Str), StringComparer.Ordinal);

    // Drop non-positive ids at the assignee boundary — the base only rejects blank ids / names, but the
    // assignee model has always treated id <= 0 as unusable.
    private static IReadOnlyList<SelectorItem> ToItems(IReadOnlyList<TaskAssignee> people)
        => people.Where(p => p.Id > 0).Select(ToItem).ToList();

    private static SelectorRow ToRow(AssigneeRow row)
        => new(Str(row.Id), row.Name, row.Selected, row.Locked, Distinguished: false);

    private static AssigneeRow ToAssigneeRow(SelectorRow row)
        => new(long.Parse(row.Id, CultureInfo.InvariantCulture), row.Name, row.Selected, row.Locked);
}
