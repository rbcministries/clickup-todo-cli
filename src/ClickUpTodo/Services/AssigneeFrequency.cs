using System.Text.Json.Serialization;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>One tallied candidate person: a stable id, the latest non-blank display name seen for
/// them, and the set of <b>distinct task ids</b> they've been seen assigned to across the loaded
/// working set. <see cref="Count"/> — the ranking key — is that distinct-task count, so re-observing
/// the same task on a later refresh never inflates it (the steady-state poll is idempotent). This is
/// also the persisted shape (see <see cref="AssigneeFrequencyCache"/>), so it stays a plain
/// serialisable record; <see cref="Count"/> is derived and excluded from serialisation.</summary>
public sealed record AssigneeFrequencyEntry(long Id, string Name, IReadOnlyList<string> TaskIds)
{
    /// <summary>Number of distinct tasks this person has been seen assigned to — the ranking weight.</summary>
    [JsonIgnore]
    public int Count => TaskIds.Count;
}

/// <summary>
/// Pure tally / ranking / matching rules backing the assignee-frequency cache (#155), kept free of
/// I/O, persistence, and Terminal.Gui so they are unit-testable in isolation — the stateful glue
/// (loading, persistence, the deferred workspace-members top-up) lives in
/// <see cref="AssigneeFrequencyCache"/>.
/// <para>
/// The pool is the assignees seen across the task lists the user has loaded, weighted by how many
/// distinct tasks each was assigned to; the Assignees pane (#158) uses it to fill its empty-state
/// list up to N rows and to back type-ahead search.
/// </para>
/// </summary>
public static class AssigneeFrequency
{
    /// <summary>
    /// Records every assignee on <paramref name="tasks"/> into <paramref name="acc"/> (id ⇒ entry):
    /// adds the task's id to that person's distinct-task set and refreshes the stored name to the
    /// latest non-blank value seen. Because it tracks distinct task ids, re-feeding a task already
    /// recorded for a person is a no-op — so calling this every refresh with the same working set does
    /// not inflate anyone's count. Assignees with a non-positive id or a blank name are ignored (an id
    /// is required to address a person for an assignee write, and a nameless row is useless to the
    /// pane), as are tasks with a blank id. Mutates <paramref name="acc"/> in place and returns
    /// <see langword="true"/> only when it actually changed, so the caller persists exactly when needed.
    /// </summary>
    public static bool Accumulate(IDictionary<long, AssigneeFrequencyEntry> acc, IEnumerable<TaskItem> tasks)
    {
        var changed = false;
        foreach (var task in tasks)
        {
            if (string.IsNullOrEmpty(task.Id))
                continue;
            foreach (var person in task.Assignees)
            {
                if (person.Id <= 0)
                    continue;
                var name = person.Name?.Trim() ?? "";
                if (name.Length == 0)
                    continue;

                if (acc.TryGetValue(person.Id, out var existing))
                {
                    var hasTask = existing.TaskIds.Contains(task.Id);
                    if (hasTask && existing.Name == name)
                        continue; // already recorded for this task, name unchanged — nothing to do.
                    var taskIds = hasTask ? existing.TaskIds : [.. existing.TaskIds, task.Id];
                    acc[person.Id] = existing with { Name = name, TaskIds = taskIds };
                }
                else
                {
                    acc[person.Id] = new AssigneeFrequencyEntry(person.Id, name, [task.Id]);
                }
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Adds <paramref name="people"/> as candidates with no tasks yet (count <c>0</c>) without
    /// disturbing anyone already tallied — an existing entry keeps its distinct-task count and its
    /// known name. Only genuinely new people (positive id, non-blank name) are inserted. Used by the
    /// deferred workspace-members top-up to fatten a thin pool. Mutates <paramref name="acc"/>; returns
    /// whether it changed.
    /// </summary>
    public static bool Seed(IDictionary<long, AssigneeFrequencyEntry> acc, IEnumerable<TaskAssignee> people)
    {
        var changed = false;
        foreach (var person in people)
        {
            if (person.Id <= 0)
                continue;
            var name = person.Name?.Trim() ?? "";
            if (name.Length == 0 || acc.ContainsKey(person.Id))
                continue;

            acc[person.Id] = new AssigneeFrequencyEntry(person.Id, name, []);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// The top <paramref name="n"/> candidates ranked by distinct-task count (desc), breaking ties by
    /// name (case-insensitive asc) then id — a total, deterministic order. Ids in
    /// <paramref name="exclude"/> (typically the task's current assignees) and blank-named entries are
    /// dropped. Returns <see cref="TaskAssignee"/>-shaped results the pane consumes directly. A
    /// non-positive <paramref name="n"/> yields an empty list.
    /// </summary>
    public static IReadOnlyList<TaskAssignee> TopMostFrequent(
        IEnumerable<AssigneeFrequencyEntry> entries, int n, ISet<long>? exclude = null)
    {
        if (n <= 0)
            return [];
        return Ranked(entries, exclude).Take(n).ToList();
    }

    /// <summary>
    /// Candidates whose name contains <paramref name="query"/> (case-insensitive substring), ranked in
    /// the same order as <see cref="TopMostFrequent"/>. A blank query returns the whole ranked pool
    /// (so the pane can show the frequency list before the user types). Blank-named entries are
    /// dropped.
    /// </summary>
    public static IReadOnlyList<TaskAssignee> Match(
        IEnumerable<AssigneeFrequencyEntry> entries, string? query, ISet<long>? exclude = null)
    {
        var term = query?.Trim() ?? "";
        var ranked = Ranked(entries, exclude);
        if (term.Length == 0)
            return ranked.ToList();
        return ranked
            .Where(a => a.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static IEnumerable<TaskAssignee> Ranked(
        IEnumerable<AssigneeFrequencyEntry> entries, ISet<long>? exclude)
        => entries
            .Where(e => (exclude is null || !exclude.Contains(e.Id)) && !string.IsNullOrWhiteSpace(e.Name))
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id)
            .Select(e => new TaskAssignee(e.Id, e.Name));
}
