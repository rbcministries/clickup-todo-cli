using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui;

/// <summary>
/// The list specialization of the reusable <see cref="SelectorView"/> base (#243): a search box over a
/// type-ahead list, built to be shared by the New Task List selector (#240) and the Quick Updates List
/// pane (#242) instead of forking the assignee selector. Empty search shows the currently-selected lists
/// (prefixed <c>✓</c>) topped up from the most-frequent pool (<see cref="Services.ListFrequencyCache"/>,
/// #238); typing runs a debounced substring match; picking a result adds that list, picking a <c>✓</c>
/// row removes it. A seeded <em>primary/home</em> list (the create target — #240) renders with a
/// <c>" (home)"</c> marker but is <b>not</b> locked: it can be removed like any other selection (the
/// "≥1 list" rule is the host's job).
/// <para>
/// All the machinery (layout, input, debounce, off-thread dispatch, optimistic apply/reconcile/revert)
/// lives in <see cref="SelectorView"/> over string ids. Lists are natively string-id'd, so — unlike
/// <see cref="AssigneeSelectorView"/>, which adapts <c>long</c> ids — this subclass's adapters are
/// near-identity <see cref="NamedEntity"/> ⇄ <see cref="SelectorItem"/> maps; it supplies only the
/// list-worded flash messages, the home marker, and the <see cref="Selection"/>/<see cref="Primary"/>
/// accessors. It's a single focusable composite, so it doesn't reintroduce a second focusable pane
/// (#3/#38); it's meant to be embedded in a modal screen that owns Tab/Esc.
/// </para>
/// </summary>
public sealed class ListSelectorView : SelectorView
{
    /// <param name="match">Substring match over the candidate pool, excluding the given ids — i.e.
    /// <c>ListFrequencyCache.Match</c>.</param>
    /// <param name="topFrequent">Top-N most-frequent candidates excluding the given ids — i.e.
    /// <c>ListFrequencyCache.TopMostFrequent</c>.</param>
    /// <param name="initialSelected">The task's current list membership (empty for a new task).</param>
    /// <param name="primary">The primary/home list — the create target — pre-selected and marked
    /// <c>" (home)"</c>; null for none. Tracked as the distinguished entry, but removable (no lock).</param>
    /// <param name="mode">Whether add/remove apply immediately or are collected for a later save.</param>
    /// <param name="applyAsync">Required in <see cref="SelectorMode.ImmediateApply"/>: performs the
    /// server add/remove for a list and returns the server-confirmed membership set.</param>
    /// <param name="timeProvider">Debounce clock (test seam); defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="debounce">Type-ahead debounce interval; defaults to ~1s.</param>
    /// <param name="capacity">Empty-state row budget (see <see cref="SelectorView.DefaultCapacity"/>).</param>
    public ListSelectorView(
        Func<string, ISet<string>, IReadOnlyList<NamedEntity>> match,
        Func<int, ISet<string>, IReadOnlyList<NamedEntity>> topFrequent,
        IReadOnlyList<NamedEntity>? initialSelected = null,
        NamedEntity? primary = null,
        SelectorMode mode = SelectorMode.CollectSelection,
        Func<ToggleKind, NamedEntity, CancellationToken, Task<IReadOnlyList<NamedEntity>>>? applyAsync = null,
        TimeProvider? timeProvider = null,
        TimeSpan? debounce = null,
        int capacity = DefaultCapacity)
        : base(
            match: AdaptMatch(match),
            topFrequent: AdaptTopFrequent(topFrequent),
            initialSelected: ToItems(initialSelected),
            lockedDefault: null,
            distinguishedDefault: ToDistinguishedItem(primary),
            distinguishedSuffix: ListSelectorModel.HomeSuffix,
            mode: mode,
            applyAsync: AdaptApply(applyAsync),
            // Lists carry no locked entry, so the base's locked-remove flash can never fire here; the
            // base default is left in place rather than passing a message that's dead by construction.
            applyFailureMessage: ex => $"Couldn't update lists: {ex.Message}",
            timeProvider: timeProvider,
            debounce: debounce,
            capacity: capacity)
    {
    }

    /// <summary>The current selection as lists, in selection order — the seeded <c>initialSelected</c>
    /// (if any) first, then the seeded <paramref name="primary"/>, then any lists picked at runtime.
    /// (For the New Task case, where <c>initialSelected</c> is empty, that makes the primary the first
    /// element; use <see cref="Primary"/> rather than <c>Selection[0]</c> to identify the create target
    /// in the general case.)</summary>
    public IReadOnlyList<NamedEntity> Selection
        => SelectedItems.Select(ListSelectorModel.ToEntity).ToList();

    /// <summary>The primary/home list — the create target. The currently-marked home list if one is
    /// still selected (kept in lockstep with the on-screen <c>" (home)"</c> marker via the base's
    /// <see cref="SelectorView.DistinguishedSelection"/>), otherwise the first selected list, otherwise
    /// null when nothing is selected. So removing the seeded home falls through to the first remaining
    /// selection rather than pointing at an unmarked list, and — in immediate-apply mode — a home the
    /// server dropped stops being reported the moment the base stops marking it. See
    /// <see cref="ListSelectorModel.ResolvePrimary"/>.</summary>
    public NamedEntity? Primary
        => ListSelectorModel.ResolvePrimary(
            Selection, DistinguishedSelection.Select(ListSelectorModel.ToEntity).ToList());

    /// <summary>
    /// Merges the task's already-existing additional list memberships into the selection without firing a
    /// server write — the Quick Updates List pane (#242) opens seeded with the home list instantly, then
    /// enriches the rare "Tasks in Multiple Lists" locations from a background <c>GetTaskDetailAsync</c>
    /// when it returns. Additive and idempotent; no-ops once the user has started editing. Must run on the
    /// UI thread. See <see cref="SelectorView.AddExistingSelections"/>.
    /// </summary>
    public void SeedExistingMemberships(IReadOnlyList<NamedEntity> lists)
        => AddExistingSelections(ToItems(lists));

    // ── list ↔ base adapters (near-identity: lists are natively string-id'd) ────

    private static IReadOnlyList<SelectorItem> ToItems(IReadOnlyList<NamedEntity>? lists)
        => (lists ?? []).Select(ListSelectorModel.ToItem).ToList();

    private static SelectorItem? ToDistinguishedItem(NamedEntity? primary)
        => primary is { } p && !string.IsNullOrWhiteSpace(p.Id) ? ListSelectorModel.ToItem(p) : null;

    private static Func<string, ISet<string>, IReadOnlyList<SelectorItem>> AdaptMatch(
        Func<string, ISet<string>, IReadOnlyList<NamedEntity>> match)
        => (query, exclude) => match(query, exclude).Select(ListSelectorModel.ToItem).ToList();

    private static Func<int, ISet<string>, IReadOnlyList<SelectorItem>> AdaptTopFrequent(
        Func<int, ISet<string>, IReadOnlyList<NamedEntity>> topFrequent)
        => (n, exclude) => topFrequent(n, exclude).Select(ListSelectorModel.ToItem).ToList();

    private static Func<ToggleKind, SelectorItem, CancellationToken, Task<IReadOnlyList<SelectorItem>>>? AdaptApply(
        Func<ToggleKind, NamedEntity, CancellationToken, Task<IReadOnlyList<NamedEntity>>>? applyAsync)
        => applyAsync is null
            ? null
            : async (kind, item, ct) =>
                (IReadOnlyList<SelectorItem>)(await applyAsync(kind, ListSelectorModel.ToEntity(item), ct).ConfigureAwait(false))
                    .Select(ListSelectorModel.ToItem).ToList();
}
