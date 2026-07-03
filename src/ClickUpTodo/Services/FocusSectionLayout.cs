using ClickUpTodo.ClickUp;
using ClickUpTodo.Configuration;

namespace ClickUpTodo.Services;

/// <summary>
/// The arranged "Current Focus" section (#75): the <paramref name="Rows"/> to render (pinned tasks as
/// top-level anchors with their in-snapshot subtasks nested beneath them) plus
/// <paramref name="NestedSubtaskIds"/> — the ids of the non-pinned subtasks that were pulled into Focus
/// and must therefore be excluded from the to-do set so they don't render twice.
/// </summary>
public readonly record struct FocusSection(
    IReadOnlyList<ArrangedRow> Rows,
    IReadOnlySet<string> NestedSubtaskIds);

/// <summary>
/// Lays out the pinned "Current Focus" section for the dashboard's single sectioned list. Pure (no
/// Terminal.Gui, no I/O) so the pinned-parent nesting rules are unit-testable; <see cref="Tui.TodoApp"/>
/// just materialises the returned rows.
/// <para>
/// When the subtasks view (<paramref name="nest"/>, F4) is on, a pinned parent's in-snapshot subtasks
/// nest indented beneath it — reusing the same pure <see cref="SubtaskArranger"/> the to-do section
/// uses — instead of falling through to the to-do set un-indented (#75). Pins deliberately ignore the
/// F3 filter/group view ("explicit pins shouldn't vanish"), so the section is arranged directly rather
/// than routed through <see cref="TaskView.Apply"/>; sort still applies, as it does to pins today.
/// </para>
/// </summary>
public static class FocusSectionLayout
{
    private static readonly Dictionary<string, TaskItem> NoContext = new();

    /// <param name="allTasks">The full task snapshot.</param>
    /// <param name="pinnedIds">Ids of the tasks pinned to Current Focus (the section's anchors).</param>
    /// <param name="nest">True when the F4 subtasks view is on (nest a pinned parent's subtasks).</param>
    /// <param name="sortField">The active sort field (applies to pins as to the to-do set).</param>
    /// <param name="sortDirection">The active sort direction.</param>
    /// <param name="expanded">
    /// The ids of expanded parents for per-parent folding (#76), forwarded to <see cref="SubtaskArranger"/>
    /// so a pinned parent folds like any other. <c>null</c> ⇒ every parent expanded (pre-#76 behaviour).
    /// </param>
    public static FocusSection Build(
        IReadOnlyList<TaskItem> allTasks,
        IReadOnlySet<string> pinnedIds,
        bool nest,
        TaskField? sortField,
        SortDirection sortDirection,
        IReadOnlySet<string>? expanded = null)
    {
        var pinned = allTasks.Where(t => pinnedIds.Contains(t.Id));

        // Subtasks view off: the Focus section is a plain flat list (parity with pre-#75). Pins show
        // as-is with no nesting, and no subtasks are pulled out of the to-do set. Deliberately skips the
        // arranger so two pins that happen to be parent/child stay flat, exactly as they did before.
        if (!nest)
        {
            var flat = TaskView.Sort(pinned, sortField, sortDirection)
                .Select(t => new ArrangedRow(t, Depth: 0, IsContextParent: false))
                .ToList();
            return new FocusSection(flat, EmptyIds);
        }

        // Direct children per parent across the whole snapshot.
        var childrenByParent = new Dictionary<string, List<TaskItem>>();
        foreach (var t in allTasks)
        {
            if (string.IsNullOrEmpty(t.ParentId))
                continue;
            if (!childrenByParent.TryGetValue(t.ParentId!, out var siblings))
                childrenByParent[t.ParentId!] = siblings = [];
            siblings.Add(t);
        }

        // Pull in every in-snapshot descendant of a pinned task (transitively — grandchildren included),
        // except tasks that are themselves pinned (they already ride in via `pinned`). These are exactly
        // the rows that must move out of the to-do set and nest under their pinned ancestor instead.
        var nested = new HashSet<string>(StringComparer.Ordinal);
        var pulledTasks = new List<TaskItem>();
        var walked = new HashSet<string>(pinnedIds, StringComparer.Ordinal);
        var stack = new Stack<string>(pinnedIds);
        while (stack.Count > 0)
        {
            if (!childrenByParent.TryGetValue(stack.Pop(), out var children))
                continue;
            foreach (var child in children)
            {
                if (!walked.Add(child.Id)) // guard against cycles / a task reachable by two paths
                    continue;
                if (!pinnedIds.Contains(child.Id))
                {
                    nested.Add(child.Id);
                    pulledTasks.Add(child);
                }
                stack.Push(child.Id); // keep descending so grandchildren of a pin are pulled too
            }
        }

        // Focus input = pinned anchors + pulled-in descendants, sorted together, then nested via the same
        // pure arranger the to-do section uses. No context parents in Focus: a pinned subtask whose parent
        // isn't pinned falls back to a flat top-level row (the arranger's orphan path), matching pre-#75.
        var focusInput = TaskView.Sort(pinned.Concat(pulledTasks), sortField, sortDirection);
        var rows = SubtaskArranger.Arrange(focusInput, NoContext, expanded);
        return new FocusSection(rows, nested);
    }

    private static readonly IReadOnlySet<string> EmptyIds =
        new HashSet<string>(StringComparer.Ordinal);
}
