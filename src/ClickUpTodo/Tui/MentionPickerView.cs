using System.Globalization;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui;

/// <summary>
/// The <c>@</c>-mention picker (#324, sub-issue J of #313; depends on #323): a modal search-and-select
/// for choosing a workspace member to <c>@</c>-mention, built as a thin specialization of the reusable
/// <see cref="SelectorView"/> base (#243) rather than a fork — the same pattern as
/// <see cref="AssigneeSelectorView"/> (#212) and <see cref="ListSelectorView"/> (#239). Empty search
/// shows the most-frequent members; typing runs a debounced substring match; picking a member raises
/// <see cref="MemberPicked"/> with the chosen <see cref="MentionTarget"/> (<c>{ userId, displayName }</c>).
/// <para>
/// Single-select in practice — the host modal closes on the first pick — and seeded with nothing, so
/// there is no locked or distinguished entry (unlike the assignee current-user default or the list home
/// marker). The pick is keyed on the member's <c>userId</c>, never the raw typed text, so spaced display
/// names ("Ben Seymour") submit to the correct id (#323 / the base's #234 row-id discipline).
/// </para>
/// <para>
/// <b>@Brain / named Super Agents are not offered</b> as mention targets: the #321 spike hasn't
/// confirmed they are API-addressable, so per #324 they are omitted for now (revisit when #321 lands —
/// the candidate pool is an injected delegate, so a synthetic entry can be added later without touching
/// this view).
/// </para>
/// <para>
/// All the machinery (layout, input, the debounce timer, off-thread dispatch) lives in
/// <see cref="SelectorView"/>; this subclass adapts the member boundary — <c>long</c> userId /
/// <see cref="WorkspaceMember"/> ↔ the base's <see cref="SelectorItem"/> — and surfaces the picked
/// member. It's a single focusable composite, so it doesn't reintroduce a second focusable pane
/// (#3/#38); it's meant to be embedded in a modal screen that owns Tab/Esc, consistent with how the
/// assignee selector is hosted.
/// </para>
/// </summary>
public sealed class MentionPickerView : SelectorView
{
    // Ids already surfaced via MemberPicked, so a pick fires exactly once. Pruned when a member leaves
    // the selection so a later re-pick can announce again. Touched on the UI thread (SelectionChanged).
    private readonly HashSet<long> _announced = [];

    /// <param name="match">Substring match over the workspace-member pool, excluding the given ids —
    /// e.g. a member-roster filter (optionally ranked via the assignee-frequency plumbing, #155).</param>
    /// <param name="topFrequent">Top-N most-frequent members excluding the given ids.</param>
    /// <param name="timeProvider">Debounce clock (test seam); defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="debounce">Type-ahead debounce interval; defaults to ~1s.</param>
    /// <param name="capacity">Empty-state row budget (see <see cref="SelectorView.DefaultCapacity"/>).</param>
    public MentionPickerView(
        Func<string, ISet<long>, IReadOnlyList<WorkspaceMember>> match,
        Func<int, ISet<long>, IReadOnlyList<WorkspaceMember>> topFrequent,
        TimeProvider? timeProvider = null,
        TimeSpan? debounce = null,
        int capacity = DefaultCapacity)
        : base(
            match: AdaptMatch(match),
            topFrequent: AdaptTopFrequent(topFrequent),
            initialSelected: null,
            lockedDefault: null,
            distinguishedDefault: null,
            distinguishedSuffix: "",
            mode: SelectorMode.CollectSelection,
            applyAsync: null,
            timeProvider: timeProvider,
            debounce: debounce,
            capacity: capacity)
    {
        SelectionChanged += OnSelectionChanged;
    }

    /// <summary>Raised when the user picks a member — the chosen <c>{ userId, displayName }</c> for the
    /// caller to turn into a mention token/block. Fires <b>once</b> per newly-picked member and never on
    /// a de-select, so a host that closes on the first pick receives exactly one target.</summary>
    public event EventHandler<MentionTarget>? MemberPicked;

    // Map the base's current selection to mention targets and raise MemberPicked for each newly-picked
    // one — the "announce exactly once" decision lives in the pure, unit-tested MentionPickerModel. Runs
    // on the UI thread (the base raises SelectionChanged there).
    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        var current = SelectedItems.Select(MentionPickerModel.ToTarget).ToList();
        foreach (var target in MentionPickerModel.NewlyAnnounced(_announced, current))
            MemberPicked?.Invoke(this, target);
    }

    // ── member ↔ base adapters ────────────────────────────────────────────────
    // A ClickUp userId is always positive; a non-positive id is dropped here so the base (which only
    // guards blank ids / names) never sees one — mirroring AssigneeSelectorView's boundary.

    private static Func<string, ISet<string>, IReadOnlyList<SelectorItem>> AdaptMatch(
        Func<string, ISet<long>, IReadOnlyList<WorkspaceMember>> match)
        => (query, exclude) => match(query, ToLongSet(exclude)).Where(m => m.Id > 0).Select(MentionPickerModel.ToItem).ToList();

    private static Func<int, ISet<string>, IReadOnlyList<SelectorItem>> AdaptTopFrequent(
        Func<int, ISet<long>, IReadOnlyList<WorkspaceMember>> topFrequent)
        => (n, exclude) => topFrequent(n, ToLongSet(exclude)).Where(m => m.Id > 0).Select(MentionPickerModel.ToItem).ToList();

    private static ISet<long> ToLongSet(ISet<string> ids)
    {
        var result = new HashSet<long>();
        foreach (var id in ids)
            if (long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                result.Add(parsed);
        return result;
    }
}
