namespace ClickUpTodo.Agent;

/// <summary>
/// Pure, I/O-free decision for <b>whether</b> a <see cref="LaunchLocation.SplitPane"/> request should
/// actually split, or degrade to a tab because the resulting panes would be too narrow to use (#505,
/// slice C). Two 60-column panes are worse than one full-width tab, so below a floor on the <b>resulting</b>
/// pane width a split degrades — the same silent split → tab → window ladder the planner already walks,
/// but decided <i>before</i> planning because the planner has no notion of the current terminal size.
///
/// The planner (<see cref="TerminalCommandPlanner"/>) maps <i>how</i> a split is shaped; this decides
/// <i>when</i> one is worth making. The caller (the split gesture, epic #502 E/F/J) supplies the live
/// terminal width, calls <see cref="Evaluate"/>, and feeds the returned <see cref="Decision.Location"/>
/// to the planner — a viable request stays <see cref="LaunchLocation.SplitPane"/>; a non-viable one comes
/// back <see cref="LaunchLocation.NewTab"/> and the planner emits that host's NewTab ladder (a real tab
/// where the host has one, else its nearest in-place surface or a window — see
/// <see cref="TerminalCommandPlanner"/>). The <see cref="Decision.Reason"/> is a ready-to-flash line so the
/// degradation reads as deliberate rather than a silently-failed split; it stays host-agnostic (it doesn't
/// promise a "tab", since the NewTab fallback isn't a tab on every host, e.g. Zellij's in-session pane).
/// </summary>
public static class SplitViability
{
    /// <summary>
    /// The default minimum <b>resulting</b> pane width, in columns, below which a side-by-side split
    /// degrades to a tab. Derived from the task list's fixed leading chrome rather than invented: in icon
    /// mode a row spends the id chip + the four-column Status abbreviation gutter
    /// (<c>TaskRowFormatter.StatusGutter</c>) + the three-column Priority gutter (<c>BlankGutter</c>) + the
    /// fold marker ≈ 18 columns before the title even starts, and a title wants ~40 more to read — ≈ 58,
    /// rounded to 60, which is also the width the issue itself names ("two 60-column panes are worse than
    /// one tab"). A single knob so the maintainer can move it in one place; the caller may pass its own.
    /// </summary>
    public const int DefaultMinPaneColumns = 60;

    /// <summary>
    /// The outcome of a viability check: the (possibly degraded) <paramref name="Location"/> to launch,
    /// whether it <paramref name="Degraded"/> from a split to a tab, the <paramref name="ResultingColumns"/>
    /// the narrower resulting pane would have had, and a human <paramref name="Reason"/> (non-null only
    /// when it degraded) for the status line.
    /// </summary>
    public readonly record struct Decision(
        LaunchLocation Location, bool Degraded, int ResultingColumns, string? Reason);

    /// <summary>
    /// Decide whether a split of a <paramref name="terminalColumns"/>-wide pane, in
    /// <paramref name="direction"/> and giving the new pane <paramref name="sizePercent"/> of the parent
    /// (null ⇒ an even split), clears <paramref name="minPaneColumns"/>. A <see cref="SplitDirection.Below"/>
    /// (stacked) split leaves the columns whole — only rows shrink — so it never trips a <i>column</i>
    /// floor. A side-by-side split divides the columns; both resulting panes host a TUI, so the binding
    /// width is the narrower of the two (<c>min(new, ours)</c>). At or above the floor the request stays a
    /// split; below it, it degrades to <see cref="LaunchLocation.NewTab"/> with a reason.
    ///
    /// <see cref="SplitDirection.Auto"/> is treated as side-by-side here — the conservative choice, since a
    /// side-by-side split is the one that can produce unusably narrow panes. On Windows Terminal a real
    /// <c>Auto</c> split may instead <i>stack</i> (full width, always viable) by aspect ratio, so this can
    /// refuse a WT-auto stack that would in fact have fit; erring toward a tab is the safe direction.
    /// </summary>
    public static Decision Evaluate(
        int terminalColumns,
        SplitDirection direction,
        int? sizePercent = null,
        int minPaneColumns = DefaultMinPaneColumns)
    {
        // Stacking keeps the full width; the floor is a column threshold, so it can't trip.
        if (direction == SplitDirection.Below)
            return new Decision(LaunchLocation.SplitPane, Degraded: false, terminalColumns, Reason: null);

        // Side-by-side (Beside / Auto): the new pane takes `sizePercent` of the columns, our pane keeps the
        // rest. Both run a TUI, so the narrower one is what must clear the floor. Approximate — a 1-column
        // divider and per-host rounding are well within the tunable floor.
        var newShare = sizePercent is { } p ? Math.Clamp(p, 1, 99) / 100.0 : 0.5;
        var newColumns = (int)Math.Round(terminalColumns * newShare, MidpointRounding.AwayFromZero);
        var ourColumns = terminalColumns - newColumns;
        var resulting = Math.Max(0, Math.Min(newColumns, ourColumns));

        if (resulting >= minPaneColumns)
            return new Decision(LaunchLocation.SplitPane, Degraded: false, resulting, Reason: null);

        return new Decision(
            LaunchLocation.NewTab,
            Degraded: true,
            resulting,
            $"Terminal too narrow to split ({resulting}-column panes; need {minPaneColumns}) — opening elsewhere instead.");
    }
}
