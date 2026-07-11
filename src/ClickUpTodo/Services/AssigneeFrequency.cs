using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>One tallied candidate person: a stable id, the latest non-blank display name seen for
/// them, and how many times they've appeared as an assignee across the loaded task working set.
/// This is also the persisted shape (see <see cref="AssigneeFrequencyCache"/>), so it stays a plain
/// serialisable record.</summary>
public sealed record AssigneeFrequencyEntry(long Id, string Name, int Count);

/// <summary>
/// Pure tally / ranking / matching rules backing the assignee-frequency cache (#155), kept free of
/// I/O, persistence, and Terminal.Gui so they are unit-testable in isolation — the stateful glue
/// (loading, persistence, the deferred workspace-members top-up) lives in
/// <see cref="AssigneeFrequencyCache"/>.
/// <para>
/// The pool is the most-frequent assignees across the task lists the user has loaded; the Assignees
/// pane (#158) uses it to fill its empty-state list up to N rows and to back type-ahead search.
/// </para>
/// </summary>
public static class AssigneeFrequency
{
    /// <summary>
    /// Tallies every assignee on <paramref name="tasks"/> into <paramref name="acc"/> (id ⇒ entry):
    /// increments the occurrence count and refreshes the stored name to the latest non-blank value
    /// seen. Assignees with a non-positive id or a blank name are ignored (an id is required to
    /// address a person for an assignee write, and a nameless row is useless to the pane). Mutates
    /// <paramref name="acc"/> in place and returns <see langword="true"/> only when it actually
    /// changed, so the caller persists exactly when needed.
    /// </summary>
    public static bool Accumulate(IDictionary<long, AssigneeFrequencyEntry> acc, IEnumerable<TaskItem> tasks)
    {
        var changed = false;
        foreach (var task in tasks)
        {
            foreach (var person in task.Assignees)
            {
                if (person.Id <= 0)
                    continue;
                var name = person.Name?.Trim() ?? "";
                if (name.Length == 0)
                    continue;

                if (acc.TryGetValue(person.Id, out var existing))
                    acc[person.Id] = existing with { Name = name, Count = existing.Count + 1 };
                else
                    acc[person.Id] = new AssigneeFrequencyEntry(person.Id, name, 1);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Adds <paramref name="people"/> as candidates at count <c>0</c> without disturbing anyone already
    /// tallied — an existing entry keeps its real (&gt;0) count and its known name. Only genuinely new
    /// people (positive id, non-blank name) are inserted. Used by the deferred workspace-members
    /// top-up to fatten a thin pool. Mutates <paramref name="acc"/>; returns whether it changed.
    /// </summary>
    public static bool Seed(IDictionary<long, AssigneeFrequencyEntry> acc, IEnumerable<AssigneeFrequencyEntry> people)
    {
        var changed = false;
        foreach (var person in people)
        {
            if (person.Id <= 0)
                continue;
            var name = person.Name?.Trim() ?? "";
            if (name.Length == 0 || acc.ContainsKey(person.Id))
                continue;

            acc[person.Id] = new AssigneeFrequencyEntry(person.Id, name, 0);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// The top <paramref name="n"/> candidates ranked by occurrence (count desc), breaking ties by
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
