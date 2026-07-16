using System.Text.Json.Serialization;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>One tallied candidate list: a stable (string) id, the latest non-blank display name seen
/// for it, and the set of <b>distinct task ids</b> it's been seen hosting across the loaded working
/// set. <see cref="Count"/> — the ranking key — is that distinct-task count, so re-observing the same
/// task on a later refresh never inflates it (the steady-state poll is idempotent). This is also the
/// persisted shape (see <see cref="ListFrequencyCache"/>), so it stays a plain serialisable record;
/// <see cref="Count"/> is derived and excluded from serialisation. Mirrors
/// <see cref="AssigneeFrequencyEntry"/>, keyed by a list's string id.</summary>
public sealed record ListFrequencyEntry(string Id, string Name, IReadOnlyList<string> TaskIds)
{
    /// <summary>Number of distinct tasks this list has been seen hosting — the ranking weight.</summary>
    [JsonIgnore]
    public int Count => TaskIds.Count;
}

/// <summary>
/// Pure tally / ranking / matching rules backing the list-frequency cache (#238), kept free of I/O,
/// persistence, and Terminal.Gui so they are unit-testable in isolation — the stateful glue (loading,
/// persistence, the scheduled-walk seed intake) lives in <see cref="ListFrequencyCache"/>.
/// <para>
/// The pool is the lists seen across the tasks the user has loaded, weighted by how many distinct
/// tasks each hosted; the future List selector (#239/#240) uses it to fill its empty-state list up to
/// N rows and to back type-ahead search — exactly as the Assignees pane does with people (#155). A
/// faithful mirror of <see cref="AssigneeFrequency"/>, keyed by <b>string list id</b> and returning
/// <see cref="NamedEntity"/>.
/// </para>
/// </summary>
public static class ListFrequency
{
    /// <summary>
    /// Records the (single home) list of every task in <paramref name="tasks"/> into
    /// <paramref name="acc"/> (id ⇒ entry): adds the task's id to that list's distinct-task set and
    /// refreshes the stored name to the latest non-blank value seen. Because it tracks distinct task
    /// ids, re-feeding a task already recorded for a list is a no-op — so calling this every refresh
    /// with the same working set does not inflate any list's count. Tasks with a blank id, a blank
    /// <see cref="TaskItem.ListId"/>, or a blank <see cref="TaskItem.ListName"/> are ignored (an id is
    /// required to address the list, and a nameless row is useless to the selector). Mutates
    /// <paramref name="acc"/> in place and returns <see langword="true"/> only when it actually
    /// changed, so the caller persists exactly when needed.
    /// </summary>
    public static bool Accumulate(IDictionary<string, ListFrequencyEntry> acc, IEnumerable<TaskItem> tasks)
    {
        var changed = false;
        foreach (var task in tasks)
        {
            if (string.IsNullOrEmpty(task.Id))
                continue;
            var id = task.ListId?.Trim() ?? "";
            if (id.Length == 0)
                continue;
            var name = task.ListName?.Trim() ?? "";
            if (name.Length == 0)
                continue;

            if (acc.TryGetValue(id, out var existing))
            {
                var hasTask = existing.TaskIds.Contains(task.Id);
                if (hasTask && existing.Name == name)
                    continue; // already recorded for this task, name unchanged — nothing to do.
                var taskIds = hasTask ? existing.TaskIds : [.. existing.TaskIds, task.Id];
                acc[id] = existing with { Name = name, TaskIds = taskIds };
            }
            else
            {
                acc[id] = new ListFrequencyEntry(id, name, [task.Id]);
            }
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Adds <paramref name="lists"/> as candidates with no tasks yet (count <c>0</c>) without
    /// disturbing anyone already tallied — an existing entry keeps its distinct-task count and its
    /// known name. Only genuinely new lists (non-blank id, non-blank name) are inserted. Used by the
    /// scheduled workspace list-hierarchy walk (#236) to seed the long tail of lists the task feed
    /// doesn't surface, so they're searchable/selectable even when not in view. Mutates
    /// <paramref name="acc"/>; returns whether it changed.
    /// </summary>
    public static bool Seed(IDictionary<string, ListFrequencyEntry> acc, IEnumerable<NamedEntity> lists)
    {
        var changed = false;
        foreach (var list in lists)
        {
            var id = list.Id?.Trim() ?? "";
            var name = list.Name?.Trim() ?? "";
            if (id.Length == 0 || name.Length == 0 || acc.ContainsKey(id))
                continue;

            acc[id] = new ListFrequencyEntry(id, name, []);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// The top <paramref name="n"/> candidates ranked by distinct-task count (desc), breaking ties by
    /// name (case-insensitive asc) then id (ordinal) — a total, deterministic order. Ids in
    /// <paramref name="exclude"/> (typically the task's current lists) and blank-named entries are
    /// dropped. Returns <see cref="NamedEntity"/>-shaped results the selector consumes directly. A
    /// non-positive <paramref name="n"/> yields an empty list.
    /// </summary>
    public static IReadOnlyList<NamedEntity> TopMostFrequent(
        IEnumerable<ListFrequencyEntry> entries, int n, ISet<string>? exclude = null)
    {
        if (n <= 0)
            return [];
        return Ranked(entries, exclude).Take(n).ToList();
    }

    /// <summary>
    /// Candidates whose name contains <paramref name="query"/> (case-insensitive substring), ranked in
    /// the same order as <see cref="TopMostFrequent"/>. A blank query returns the whole ranked pool (so
    /// the selector can show the frequency list before the user types). Blank-named entries are
    /// dropped.
    /// </summary>
    public static IReadOnlyList<NamedEntity> Match(
        IEnumerable<ListFrequencyEntry> entries, string? query, ISet<string>? exclude = null)
    {
        var term = query?.Trim() ?? "";
        var ranked = Ranked(entries, exclude);
        if (term.Length == 0)
            return ranked.ToList();
        return ranked
            .Where(l => l.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static IEnumerable<NamedEntity> Ranked(
        IEnumerable<ListFrequencyEntry> entries, ISet<string>? exclude)
        => entries
            .Where(e => (exclude is null || !exclude.Contains(e.Id)) && !string.IsNullOrWhiteSpace(e.Name))
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => new NamedEntity(e.Id, e.Name));
}
