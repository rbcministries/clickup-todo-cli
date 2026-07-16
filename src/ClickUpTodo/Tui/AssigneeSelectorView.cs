using System.Globalization;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui;

/// <summary>
/// The assignee specialization of the reusable <see cref="SelectorView"/> base (#212, refactored onto
/// the shared base in #243): a search box over a type-ahead list, shared by the New Task screen (#213)
/// and the Quick Updates Assignees pane (#158). Empty search shows the current assignees (prefixed
/// <c>✓</c>) topped up from the most-frequent pool (#155); typing runs a debounced substring match;
/// picking a result adds that person, picking a <c>✓</c> row removes them. A seeded <em>locked</em>
/// default (e.g. the current user on a New Task) can't be removed.
/// <para>
/// All the machinery (layout, input, debounce, off-thread dispatch, optimistic apply/reconcile/revert)
/// lives in <see cref="SelectorView"/> over string ids; this thin subclass adapts the assignee
/// boundary — <c>long</c> ids / <see cref="TaskAssignee"/> ↔ the base's <see cref="SelectorItem"/> —
/// and supplies the assignee-worded flash messages. Assignees carry no distinguished marker.
/// </para>
/// </summary>
public sealed class AssigneeSelectorView : SelectorView
{
    /// <param name="match">Substring match over the candidate pool, excluding the given ids — i.e.
    /// <c>AssigneeFrequencyCache.Match</c>.</param>
    /// <param name="topFrequent">Top-N most-frequent candidates excluding the given ids — i.e.
    /// <c>AssigneeFrequencyCache.TopMostFrequent</c>.</param>
    /// <param name="initialSelected">The task's current assignees (empty for a new task).</param>
    /// <param name="lockedDefault">A default assignee that is pre-selected and cannot be removed (e.g.
    /// the current user on a New Task); null for no lock.</param>
    /// <param name="mode">Whether add/remove apply immediately or are collected for a later save.</param>
    /// <param name="applyAsync">Required in <see cref="SelectorMode.ImmediateApply"/>: performs the
    /// server add/remove for a person and returns the server-confirmed assignee set.</param>
    /// <param name="timeProvider">Debounce clock (test seam); defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="debounce">Type-ahead debounce interval; defaults to ~1s.</param>
    /// <param name="capacity">Empty-state row budget (see <see cref="SelectorView.DefaultCapacity"/>).</param>
    public AssigneeSelectorView(
        Func<string, ISet<long>, IReadOnlyList<TaskAssignee>> match,
        Func<int, ISet<long>, IReadOnlyList<TaskAssignee>> topFrequent,
        IReadOnlyList<TaskAssignee>? initialSelected = null,
        TaskAssignee? lockedDefault = null,
        SelectorMode mode = SelectorMode.CollectSelection,
        Func<ToggleKind, TaskAssignee, CancellationToken, Task<IReadOnlyList<TaskAssignee>>>? applyAsync = null,
        TimeProvider? timeProvider = null,
        TimeSpan? debounce = null,
        int capacity = DefaultCapacity)
        : base(
            match: AdaptMatch(match),
            topFrequent: AdaptTopFrequent(topFrequent),
            initialSelected: ToItems(initialSelected),
            lockedDefault: ToLockedItem(lockedDefault),
            distinguishedDefault: null,
            distinguishedSuffix: "",
            mode: mode,
            applyAsync: AdaptApply(applyAsync),
            lockedRemoveMessage: item => $"{item.Name} is the default assignee and can't be removed.",
            applyFailureMessage: ex => $"Couldn't update assignees: {ex.Message}",
            timeProvider: timeProvider,
            debounce: debounce,
            capacity: capacity)
    {
    }

    /// <summary>The current selection as assignees, in add order (the locked default, then any others).</summary>
    public IReadOnlyList<TaskAssignee> Selection
        => SelectedItems.Select(AssigneeSelectorModel.ToAssignee).ToList();

    // ── assignee ↔ base adapters ──────────────────────────────────────────────
    // Assignee ids are always positive; a non-positive id is dropped here so the base (which only
    // guards blank ids / names) never sees one — preserving the pre-refactor assignee behavior.

    private static IReadOnlyList<SelectorItem> ToItems(IReadOnlyList<TaskAssignee>? people)
        => (people ?? []).Where(p => p.Id > 0).Select(AssigneeSelectorModel.ToItem).ToList();

    private static SelectorItem? ToLockedItem(TaskAssignee? locked)
        => locked is { Id: > 0 } present ? AssigneeSelectorModel.ToItem(present) : null;

    private static Func<string, ISet<string>, IReadOnlyList<SelectorItem>> AdaptMatch(
        Func<string, ISet<long>, IReadOnlyList<TaskAssignee>> match)
        => (query, exclude) => match(query, ToLongSet(exclude)).Where(p => p.Id > 0).Select(AssigneeSelectorModel.ToItem).ToList();

    private static Func<int, ISet<string>, IReadOnlyList<SelectorItem>> AdaptTopFrequent(
        Func<int, ISet<long>, IReadOnlyList<TaskAssignee>> topFrequent)
        => (n, exclude) => topFrequent(n, ToLongSet(exclude)).Where(p => p.Id > 0).Select(AssigneeSelectorModel.ToItem).ToList();

    private static Func<ToggleKind, SelectorItem, CancellationToken, Task<IReadOnlyList<SelectorItem>>>? AdaptApply(
        Func<ToggleKind, TaskAssignee, CancellationToken, Task<IReadOnlyList<TaskAssignee>>>? applyAsync)
        => applyAsync is null
            ? null
            : async (kind, item, ct) =>
                (IReadOnlyList<SelectorItem>)(await applyAsync(kind, AssigneeSelectorModel.ToAssignee(item), ct).ConfigureAwait(false))
                    .Where(p => p.Id > 0).Select(AssigneeSelectorModel.ToItem).ToList();

    private static ISet<long> ToLongSet(ISet<string> ids)
        => new HashSet<long>(ids.Select(id => long.Parse(id, CultureInfo.InvariantCulture)));
}
