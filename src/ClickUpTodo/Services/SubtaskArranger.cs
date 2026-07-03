using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>The fold state of a row in the nested subtasks view (#76): <see cref="None"/> for a leaf, a
/// parentless task, or a context parent (none of which the user folds); otherwise <see cref="Collapsed"/>
/// (its subtree is hidden) or <see cref="Expanded"/> (its subtree is shown beneath it).</summary>
public enum FoldState
{
    None,
    Collapsed,
    Expanded,
}

/// <summary>
/// A task placed into the nested subtasks view (#46): the task, its indent <paramref name="Depth"/>
/// (0 = top level), and whether it's a <paramref name="IsContextParent"/> — a parent pulled in purely
/// as a grouping header because it isn't in the snapshot itself (i.e. not assigned to the user).
/// </summary>
public readonly record struct ArrangedRow(TaskItem Task, int Depth, bool IsContextParent)
{
    /// <summary>This row's fold marker state (#76): <see cref="FoldState.None"/> unless the row is a
    /// user-foldable parent with children in the section.</summary>
    public FoldState Fold { get; init; }
}

/// <summary>
/// Rearranges an already-filtered-and-sorted task list so each subtask sits immediately beneath its
/// parent, indented. Pure (no Terminal.Gui, no I/O) so the nesting rules are unit-testable.
/// <para>
/// Top-level anchors keep their incoming order; a task's descendants follow it (recursively, so deeper
/// subtasks indent further). A subtask whose parent isn't in the list is nested under a resolved
/// <c>contextParents</c> entry when one exists (injected once, at its first child's position), and
/// otherwise falls back to appearing flat at top level.
/// </para>
/// </summary>
public static class SubtaskArranger
{
    /// <param name="orderedTasks">The section's tasks, already in final display order.</param>
    /// <param name="contextParents">
    /// Parents referenced by a subtask but absent from <paramref name="orderedTasks"/>, keyed by id,
    /// to inject as context headers. Pass an empty dictionary to disable injection.
    /// </param>
    /// <param name="expanded">
    /// The ids of parents whose subtasks should be shown (#76). <c>null</c> means <b>every</b> parent is
    /// expanded — the pre-#76 behaviour. When non-null, a parent whose id isn't in the set is
    /// <b>collapsed</b>: its whole subtree is hidden (and suppressed, so it never leaks out flat).
    /// Context parents are always expanded regardless of the set — they exist only to display their
    /// child, so they're never user-foldable.
    /// </param>
    public static IReadOnlyList<ArrangedRow> Arrange(
        IReadOnlyList<TaskItem> orderedTasks,
        IReadOnlyDictionary<string, TaskItem> contextParents,
        IReadOnlySet<string>? expanded = null)
    {
        var present = new HashSet<string>(orderedTasks.Select(t => t.Id));

        // Direct children per parent id, preserving the incoming order among siblings.
        var childrenByParent = new Dictionary<string, List<TaskItem>>();
        foreach (var t in orderedTasks)
        {
            if (string.IsNullOrEmpty(t.ParentId))
                continue;
            if (!childrenByParent.TryGetValue(t.ParentId, out var siblings))
                childrenByParent[t.ParentId] = siblings = [];
            siblings.Add(t);
        }

        var result = new List<ArrangedRow>(orderedTasks.Count);
        var emitted = new HashSet<string>();

        // Mark a collapsed parent's whole subtree as emitted without adding rows, so the outer loop and
        // the straggler safety net don't re-surface the hidden children as flat top-level rows (#76).
        void Suppress(TaskItem task)
        {
            if (!emitted.Add(task.Id))
                return;
            if (childrenByParent.TryGetValue(task.Id, out var children))
                foreach (var child in children)
                    Suppress(child);
        }

        void Emit(TaskItem task, int depth)
        {
            // Guard first so a (pathological) parent cycle can't recurse forever.
            if (!emitted.Add(task.Id))
                return;
            var hasChildren = childrenByParent.TryGetValue(task.Id, out var children) && children.Count > 0;
            var isExpanded = expanded is null || expanded.Contains(task.Id);
            var fold = hasChildren ? (isExpanded ? FoldState.Expanded : FoldState.Collapsed) : FoldState.None;
            result.Add(new ArrangedRow(task, depth, IsContextParent: false) { Fold = fold });
            if (hasChildren)
            {
                if (isExpanded)
                    foreach (var child in children!)
                        Emit(child, depth + 1);
                else
                    foreach (var child in children!)
                        Suppress(child);
            }
        }

        var emittedContext = new HashSet<string>();
        foreach (var t in orderedTasks)
        {
            if (emitted.Contains(t.Id))
                continue;

            var parentId = t.ParentId;
            var parentInSection = !string.IsNullOrEmpty(parentId) && present.Contains(parentId!);
            if (parentInSection)
                continue; // emitted (recursively) when we reach its in-section ancestor

            var contextParent = !string.IsNullOrEmpty(parentId)
                                 && contextParents.TryGetValue(parentId!, out var cp)
                ? cp
                : null;

            if (contextParent is not null)
            {
                // Inject the not-in-snapshot parent once, as a context header, at its first child.
                if (emittedContext.Add(parentId!))
                {
                    result.Add(new ArrangedRow(contextParent, Depth: 0, IsContextParent: true));
                    foreach (var child in childrenByParent[parentId!])
                        Emit(child, depth: 1);
                }
            }
            else
            {
                // Genuine top-level task, or an orphan whose parent is entirely unknown → show flat.
                Emit(t, depth: 0);
            }
        }

        // Safety net: a task whose whole ancestor chain stays inside the section with no root anchor
        // (only possible with a parent cycle, which ClickUp doesn't produce) would otherwise be
        // dropped. Emit any stragglers at top level so every input task appears exactly once.
        foreach (var t in orderedTasks)
            if (!emitted.Contains(t.Id))
                Emit(t, depth: 0);

        return result;
    }
}
