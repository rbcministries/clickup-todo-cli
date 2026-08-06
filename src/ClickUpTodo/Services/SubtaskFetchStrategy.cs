using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Services;

/// <summary>
/// Tunables for <see cref="SubtaskFetchStrategy"/>. Defaults favour the cheap per-parent path for the
/// common small-workspace case and only switch a list to a whole-list fetch once enough in-view parents
/// cluster in it that one list round-trip beats the per-parent calls it replaces.
/// </summary>
/// <param name="PerParentThreshold">
/// When the total number of candidate parents is at or below this, always fetch per-parent (minimal
/// payload, cross-list correct) — no whole-list fetches. Keeps the common case identical to #84.
/// </param>
/// <param name="WholeListMinParents">
/// A list holding at least this many in-view parents is routed to a single whole-list fetch instead of
/// one per-parent fetch per parent in it. Must be &gt;= 2 to ever be a win; clamped to 2 if lower.
/// </param>
/// <param name="MaxWholeListFetches">Hard cap on distinct whole-list round-trips per resolve.</param>
/// <param name="MaxPerParentFetches">Hard cap on distinct per-parent round-trips per resolve.</param>
public sealed record SubtaskFetchOptions(
    int PerParentThreshold = 8,
    int WholeListMinParents = 4,
    int MaxWholeListFetches = 20,
    int MaxPerParentFetches = 60)
{
    /// <summary>Shared default instance.</summary>
    public static readonly SubtaskFetchOptions Default = new();
}

/// <summary>
/// The chosen fetch shape for resolving a snapshot's foreign subtasks (#87): which lists to pull whole
/// (<see cref="WholeListIds"/>, one round-trip each, covers all their in-view parents) and which parents
/// to pull individually (<see cref="PerParentIds"/>, BFS per id). <see cref="Truncated"/> is set when a
/// cap dropped work so the caller can log it rather than silently under-fetching.
/// </summary>
public sealed record SubtaskFetchPlan(
    IReadOnlyList<string> WholeListIds,
    IReadOnlyList<string> PerParentIds,
    bool Truncated);

/// <summary>
/// Result of <see cref="TaskService.ResolveForeignSubtasksAsync"/>: the foreign subtasks to nest, plus
/// whether a fetch cap dropped work (<see cref="Truncated"/>) so the caller can tell the user some
/// subtasks were omitted rather than truncating silently (#87).
/// <para>
/// <see cref="CompleteChildren"/> (#450) maps a parent id to its <b>complete</b> direct-children set, one
/// entry per parent the <em>per-parent</em> branch actually fetched (a successful
/// <see cref="IClickUpClient.GetSubtasksAsync"/> returns every child regardless of assignee, so it is a
/// trustworthy complete set). It is the source the Task Tree tab seeds its descendant BFS from
/// (<see cref="TaskService.BuildChildrenIndex"/>): parents resolved only by the whole-list branch — which
/// can't vouch a parent's children per-parent — and parents whose fetch failed are deliberately
/// <b>absent</b>, so a consumer never mistakes an incomplete set for a complete one.
/// </para>
/// </summary>
public sealed record ForeignSubtaskResolution(
    IReadOnlyList<TaskItem> Subtasks,
    bool Truncated,
    IReadOnlyDictionary<string, IReadOnlyList<TaskItem>> CompleteChildren);

/// <summary>
/// Pure, adaptive selector for <em>how</em> to fetch a parent's teammate-owned subtasks (#87). The pure
/// <em>selection</em> of which fetched tasks to keep stays in <see cref="TaskService.ForeignDescendants"/>;
/// this decides only the fetch <em>source/shape</em> from the shape of the snapshot, primarily the number
/// of candidate parents and how they cluster across lists:
/// <list type="bullet">
///   <item>Few parents -> one fetch per parent (minimal data, cross-list correct).</item>
///   <item>Many parents clustered in a few lists -> one whole-list fetch per dense list.</item>
///   <item>Otherwise -> per-parent for the sparse remainder.</item>
/// </list>
/// Worst cases are bounded by the option caps; <see cref="SubtaskFetchPlan.Truncated"/> flags any drop.
/// </summary>
public static class SubtaskFetchStrategy
{
    /// <summary>
    /// Decide the fetch plan for <paramref name="snapshot"/>. Deterministic: whole-list ids are ordered by
    /// (in-view parent count desc, list id asc) and per-parent ids in stable snapshot order, so caps and
    /// results are stable and unit-testable.
    /// </summary>
    public static SubtaskFetchPlan Plan(IReadOnlyList<TaskItem> snapshot, SubtaskFetchOptions? options = null)
    {
        var opts = options ?? SubtaskFetchOptions.Default;
        var minParents = Math.Max(2, opts.WholeListMinParents);

        // Distinct candidate parents in stable first-appearance order (a repeated id counts once).
        var parents = new List<TaskItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in snapshot)
        {
            if (string.IsNullOrEmpty(t.Id) || !seen.Add(t.Id))
                continue;
            parents.Add(t);
        }

        if (parents.Count == 0)
            return new SubtaskFetchPlan([], [], Truncated: false);

        // Few parents: stay per-parent — cheapest data and cross-list correct. Identical to #84's behaviour.
        if (parents.Count <= opts.PerParentThreshold)
            return Cap([], parents.Select(p => p.Id).ToList(), opts);

        // Group parents by list to find dense clusters worth a single whole-list fetch.
        var byList = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in parents)
        {
            if (string.IsNullOrEmpty(p.ListId))
                continue; // no known list -> can only be reached per-parent
            byList[p.ListId] = byList.TryGetValue(p.ListId, out var n) ? n + 1 : 1;
        }

        var wholeLists = byList
            .Where(kv => kv.Value >= minParents)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key)
            .ToList();
        var wholeSet = new HashSet<string>(wholeLists, StringComparer.Ordinal);

        // Every parent not covered by a whole-list fetch of its own list is fetched per-parent.
        var perParent = parents
            .Where(p => string.IsNullOrEmpty(p.ListId) || !wholeSet.Contains(p.ListId))
            .Select(p => p.Id)
            .ToList();

        return Cap(wholeLists, perParent, opts);
    }

    // Apply the round-trip caps, preserving the (already deterministic) input order, and flag truncation.
    private static SubtaskFetchPlan Cap(List<string> wholeLists, List<string> perParent, SubtaskFetchOptions opts)
    {
        var truncated = wholeLists.Count > opts.MaxWholeListFetches || perParent.Count > opts.MaxPerParentFetches;
        if (wholeLists.Count > opts.MaxWholeListFetches)
            wholeLists = wholeLists.Take(opts.MaxWholeListFetches).ToList();
        if (perParent.Count > opts.MaxPerParentFetches)
            perParent = perParent.Take(opts.MaxPerParentFetches).ToList();
        return new SubtaskFetchPlan(wholeLists, perParent, truncated);
    }
}
