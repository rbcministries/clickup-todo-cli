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
    /// <param name="suppressTopLevel">
    /// Ids that must <b>never</b> surface as a flat top-level (depth-0) row — in practice the
    /// teammate-owned subtasks pulled in only to nest under an in-view parent (#70). One that would
    /// otherwise fall through to the top level — because its parent isn't in this section (filtered out by
    /// a <c>Status IS NOT</c> rule, or bucketed into a different F3 group) and isn't a context parent — is
    /// skipped as a row rather than leaked un-indented as "(not assigned to you)", which would defeat the
    /// parent's filter (#172). Only the anchorless fall-through is suppressed: a suppressed id still nests
    /// normally when its parent <em>is</em> present in the section, and any <em>non</em>-suppressed
    /// descendant of a skipped orphan (e.g. the user's own assigned subtask nested under a teammate's) is
    /// re-anchored flat rather than hidden with the foreign chain. <c>null</c>/empty ⇒ no suppression
    /// (pre-#172 behaviour).
    /// </param>
    public static IReadOnlyList<ArrangedRow> Arrange(
        IReadOnlyList<TaskItem> orderedTasks,
        IReadOnlyDictionary<string, TaskItem> contextParents,
        IReadOnlySet<string>? expanded = null,
        IReadOnlySet<string>? suppressTopLevel = null)
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

        // Skip a pulled-in subtask (#70) that has no visible anchor as a *row*, while still placing its
        // descendants: a foreign descendant is likewise skipped, but a non-foreign one (e.g. the user's
        // own assigned subtask nested under a teammate's) is re-anchored flat at top level rather than
        // vanishing with the foreign chain (#172). Only called when suppressTopLevel is non-null. Guards
        // on emitted so a parent cycle can't recurse forever.
        void SkipForeignOrphan(TaskItem task)
        {
            if (!emitted.Add(task.Id))
                return;
            if (!childrenByParent.TryGetValue(task.Id, out var children))
                return;
            foreach (var child in children)
                if (suppressTopLevel!.Contains(child.Id))
                    SkipForeignOrphan(child);
                else
                    Emit(child, depth: 0); // a non-foreign descendant keeps its own subtree, re-rooted flat
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
            else if (!string.IsNullOrEmpty(parentId)
                     && suppressTopLevel is not null && suppressTopLevel.Contains(t.Id))
            {
                // A pulled-in subtask (#70) whose parent isn't visible in this section (filtered out, or
                // in another F3 group): it has a parent, just not one to nest under here, so skip it as a
                // row rather than leak it flat as a top-level "(not assigned to you)" item (#172). Its own
                // non-foreign descendants are still re-anchored by SkipForeignOrphan. A genuinely
                // parentless task is never suppressed — it's a legitimate anchor, not an orphan.
                SkipForeignOrphan(t);
            }
            else
            {
                // Genuine top-level task, or an orphan whose parent is entirely unknown → show flat.
                Emit(t, depth: 0);
            }
        }

        // Safety net: a task whose whole ancestor chain stays inside the section with no root anchor
        // (only possible with a parent cycle, which ClickUp doesn't produce) would otherwise be
        // dropped. Emit any stragglers at top level so every input task appears exactly once — unless
        // suppressed, in which case they stay hidden (a suppressed id must never surface flat, #172).
        foreach (var t in orderedTasks)
            if (!emitted.Contains(t.Id))
            {
                if (suppressTopLevel is not null && suppressTopLevel.Contains(t.Id))
                    SkipForeignOrphan(t);
                else
                    Emit(t, depth: 0);
            }

        return result;
    }

    /// <summary>
    /// The ids of every <b>user-foldable parent</b> in <paramref name="tasks"/> — a task that is present
    /// in the set and is the <b>direct</b> parent of at least one other present task. These are exactly the
    /// ids a non-null <c>expanded</c> set toggles in <see cref="Arrange"/>: a present task with a present
    /// child is emitted as a real row and marked <see cref="FoldState.Collapsed"/>/<see cref="FoldState.Expanded"/>.
    /// Such a parent may itself sit at any depth (a foldable parent can be the child of another), so
    /// expanding the whole set reveals every level — which is what lets "expand all" reach parents whose
    /// collapsed subtree isn't in the rendered rows (#83). Context parents (#46) are absent from
    /// <paramref name="tasks"/>, so they're never included — they exist only to display a child and are
    /// never user-foldable. Independent of any current fold state; ordinal comparer to match the caller's
    /// expanded-id set.
    /// </summary>
    public static IReadOnlySet<string> FoldableParentIds(IReadOnlyList<TaskItem> tasks)
    {
        var present = new HashSet<string>(tasks.Select(t => t.Id), StringComparer.Ordinal);
        var foldable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in tasks)
            if (!string.IsNullOrEmpty(t.ParentId) && present.Contains(t.ParentId!))
                foldable.Add(t.ParentId!);
        return foldable;
    }

    /// <summary>
    /// The top-most ancestor of <paramref name="id"/> reachable within <paramref name="tasks"/>: walk up
    /// the parent chain, stopping at the first task whose parent isn't present in the set — a genuine
    /// top-level task, or one whose parent is a not-in-set context parent (#46, always shown). Returns
    /// <paramref name="id"/> itself when it has no in-set parent. Cycle-safe (visits each id at most once).
    /// Lets "collapse all" land the cursor on a row that stays visible once every parent folds (#83).
    /// Dup-safe over the id column (last wins), so a caller needn't pre-dedupe the set.
    /// </summary>
    public static string TopLevelAncestorId(IReadOnlyList<TaskItem> tasks, string id)
    {
        var byId = new Dictionary<string, TaskItem>(tasks.Count, StringComparer.Ordinal);
        foreach (var t in tasks)
            byId[t.Id] = t;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = id;
        while (seen.Add(current)
               && byId.TryGetValue(current, out var task)
               && !string.IsNullOrEmpty(task.ParentId)
               && byId.ContainsKey(task.ParentId!))
            current = task.ParentId!;
        return current;
    }
}
