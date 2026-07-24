using System.Globalization;
using ClickUpTodo.ClickUp;

namespace ClickUpTodo.Tui;

/// <summary>The member chosen in the <c>@</c>-mention picker: the numeric ClickUp <c>userId</c> to
/// embed in a mention payload and the display name to render. What the picker (#324) hands its caller
/// — the comment composer (#325) / description editor (#326) — to turn into a mention token/block. The
/// id comes from the chosen member, never the raw typed text (see <see cref="MemberResolver"/>, #323).</summary>
public readonly record struct MentionTarget(long UserId, string DisplayName);

/// <summary>The decision for toggling one candidate member: what should happen, and to whom.</summary>
public readonly record struct MemberToggleResult(ToggleKind Kind, long Id);

/// <summary>One rendered row of the mention picker: the member, and whether they're currently the
/// chosen entry (shown with a <c>✓</c>). Mentions carry <b>no</b> locked/undeletable entry and no
/// distinguished entry — the picker opens with nothing selected and is single-select in practice.</summary>
public readonly record struct MemberRow(long Id, string Name, bool Selected);

/// <summary>
/// The member-typed façade over the generic <see cref="SelectorModel"/> (#243) backing the
/// <c>@</c>-mention picker (<see cref="MentionPickerView"/>, #324; depends on #323). Keeps the mention
/// boundary in <see cref="WorkspaceMember"/> / <c>long</c> userId — adapting to the base's string-id
/// <see cref="SelectorItem"/> here — so the comment composer (#325) and description editor (#326) call
/// sites and their tests stay in member terms while the pure selection logic lives in one shared place
/// (mirrors <see cref="AssigneeSelectorModel"/> / <see cref="ListSelectorModel"/>).
/// <para>
/// Rows display <see cref="WorkspaceMember.DisplayName"/> (#323 — guaranteed non-blank, spaced names
/// intact) and carry the numeric <c>userId</c> as the id; picking one yields a <see cref="MentionTarget"/>
/// keyed on that id, never the raw typed query. Member ids are always positive; a non-positive id is
/// dropped at this boundary (the base only guards blank ids / names). Mentions have no locked or
/// distinguished entry, so those base sets are always empty here.
/// </para>
/// </summary>
public static class MentionPickerModel
{
    /// <summary>The display text for a row: a leading <c>✓</c> when selected, else a two-column blank
    /// indent. Delegates to <see cref="SelectorModel.Format"/> (mentions carry no distinguished
    /// marker).</summary>
    public static string Format(MemberRow row) => SelectorModel.Format(ToRow(row));

    /// <summary>The empty-search rows: any currently-chosen member(s) first (marked <c>✓</c>), then the
    /// most-frequent <paramref name="topFrequent"/> candidates that aren't already chosen, up to
    /// <paramref name="capacity"/>. In practice the picker opens with nothing selected, so this is just
    /// the ranked candidate pool. See <see cref="SelectorModel.EmptyStateRows"/>.</summary>
    public static IReadOnlyList<MemberRow> EmptyStateRows(
        IReadOnlyList<WorkspaceMember> selected,
        IReadOnlyList<WorkspaceMember> topFrequent,
        int capacity)
        => SelectorModel.EmptyStateRows(
                ToItems(selected), EmptyStringSet, EmptyStringSet, ToItems(topFrequent), capacity)
            .Select(ToMemberRow)
            .ToList();

    /// <summary>The type-ahead rows: <paramref name="matches"/> as unselected rows, excluding any member
    /// already in <paramref name="selectedIds"/>. See <see cref="SelectorModel.SearchResultRows"/>.</summary>
    public static IReadOnlyList<MemberRow> SearchResultRows(
        IReadOnlyList<WorkspaceMember> matches, ISet<long> selectedIds)
        => SelectorModel.SearchResultRows(ToItems(matches), ToStringSet(selectedIds))
            .Select(ToMemberRow)
            .ToList();

    /// <summary>The add/remove decision for picking member <paramref name="id"/>. Mentions carry no
    /// locked entry, so this is a plain add/remove — a chosen member is always removable and the result
    /// is never <see cref="ToggleKind.LockedNoOp"/>. See <see cref="SelectorModel.Toggle"/>. The passed
    /// set is not modified.</summary>
    public static MemberToggleResult Toggle(ISet<long> selectedIds, long id)
    {
        var decision = SelectorModel.Toggle(ToStringSet(selectedIds), EmptyStringSet, Str(id));
        return new MemberToggleResult(decision.Kind, id);
    }

    /// <summary>Whether a debounce timer captured at <paramref name="capturedStamp"/> still represents
    /// the latest keystroke (<paramref name="currentStamp"/>). See
    /// <see cref="SelectorModel.ShouldRunSearch"/>.</summary>
    public static bool ShouldRunSearch(long capturedStamp, long currentStamp)
        => SelectorModel.ShouldRunSearch(capturedStamp, currentStamp);

    /// <summary>
    /// The mention targets to raise for the current selection, deciding which picks are <b>new</b> —
    /// the pure set arithmetic behind <see cref="MentionPickerView.MemberPicked"/>, kept here (not in the
    /// CI-untestable view) so "announce each pick exactly once" is unit-tested. Mutates
    /// <paramref name="announced"/> in place to the post-call state: first forgets any id no longer in
    /// <paramref name="currentSelection"/> (so a member de-selected then re-picked announces again), then
    /// records and returns each still-<c>&gt; 0</c> target that wasn't already announced (in selection
    /// order). A steady call with an unchanged selection returns empty.
    /// </summary>
    public static IReadOnlyList<MentionTarget> NewlyAnnounced(
        ISet<long> announced, IReadOnlyList<MentionTarget> currentSelection)
    {
        var selectedIds = new HashSet<long>(currentSelection.Select(t => t.UserId));
        foreach (var id in announced.Where(id => !selectedIds.Contains(id)).ToList())
            announced.Remove(id);
        var fresh = new List<MentionTarget>();
        foreach (var target in currentSelection)
            if (target.UserId > 0 && announced.Add(target.UserId))
                fresh.Add(target);
        return fresh;
    }

    // ── member ↔ base conversions ─────────────────────────────────────────────

    private static readonly ISet<string> EmptyStringSet = new HashSet<string>(StringComparer.Ordinal);

    private static string Str(long id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>A <see cref="WorkspaceMember"/> as a base <see cref="SelectorItem"/>: id = the numeric
    /// <c>userId</c>, name = the #323 <see cref="WorkspaceMember.DisplayName"/> (never blank, so the
    /// picker never renders a blank row). Also used by <see cref="MentionPickerView"/> to adapt its
    /// candidate delegates onto the base.</summary>
    internal static SelectorItem ToItem(WorkspaceMember member) => new(Str(member.Id), member.DisplayName);

    /// <summary>The <see cref="MentionTarget"/> for a member in hand — id = its own <c>userId</c>, no
    /// matching involved. A convenience for a caller that already holds a <see cref="WorkspaceMember"/>
    /// (the K/L consumers, #325/#326); the picker itself converts the <b>picked base row</b> via the
    /// <see cref="SelectorItem"/> overload below.</summary>
    internal static MentionTarget ToTarget(WorkspaceMember member) => new(member.Id, member.DisplayName);

    /// <summary>The <see cref="MentionTarget"/> for a picked base <see cref="SelectorItem"/>: the parsed
    /// <c>userId</c> and the display name shown in the row — <b>never</b> the raw typed query, so a
    /// spaced display name ("Ben Seymour") submits to the correct id (the base keys rows on the id, the
    /// #234 discipline; the whole reason J gated on I #323).</summary>
    internal static MentionTarget ToTarget(SelectorItem item)
        => new(long.Parse(item.Id, CultureInfo.InvariantCulture), item.Name);

    /// <summary>A set of member ids as the base's string-id set.</summary>
    internal static ISet<string> ToStringSet(ISet<long> ids)
        => new HashSet<string>(ids.Select(Str), StringComparer.Ordinal);

    // Drop non-positive ids at the member boundary — the base only rejects blank ids / names, but a
    // ClickUp userId is always positive; a 0/negative id is not a real member.
    private static IReadOnlyList<SelectorItem> ToItems(IReadOnlyList<WorkspaceMember> members)
        => members.Where(m => m.Id > 0).Select(ToItem).ToList();

    private static MemberRow ToMemberRow(SelectorRow row)
        => new(long.Parse(row.Id, CultureInfo.InvariantCulture), row.Name, row.Selected);

    // A member row never carries a locked or distinguished marker.
    private static SelectorRow ToRow(MemberRow row)
        => new(Str(row.Id), row.Name, row.Selected, Locked: false, Distinguished: false);
}
