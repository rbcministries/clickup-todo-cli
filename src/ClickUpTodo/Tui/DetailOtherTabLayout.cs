namespace ClickUpTodo.Tui;

/// <summary>
/// Pure layout arithmetic for the task detail <b>Other</b> tab's split between the fixed, coloured
/// header (<see cref="DetailAttributesView"/>) and the scrollable custom-fields body
/// (<see cref="Terminal.Gui.Views.TextView"/>). Extracted from the Terminal.Gui glue so the
/// short-terminal reachability behaviour (issue #81) is unit-tested without a driver.
/// <para>
/// The coloured header stays a fixed, non-scrollable view (avoiding the hand-rolled vertical-scroll
/// logic PR #80 deliberately avoided — the #63 risk). To keep every attribute reachable on a very
/// short window, this caps the header from the bottom and reports how many trailing header lines
/// <see cref="Layout.SpilledHeaderLines"/> the caller should move into the top of the scrollable body
/// as plain text. Because the header is ordered List / [Lists] / Priority / Status / Created /
/// Last&#160;activity / Due, the lines that spill first are the <em>date</em> lines, which are never
/// coloured — so spilling them loses no colour while restoring reachability via the body's scroll.
/// </para>
/// </summary>
public static class DetailOtherTabLayout
{
    /// <summary>Blank spacer row between the coloured header and the body when everything fits (mirrors
    /// the blank line the plain <see cref="TaskDetailFormatter.OtherAttributes"/> layout renders).</summary>
    public const int GapRows = 1;

    /// <summary>Rows reserved for the scrollable body when space is tight, so the "Custom fields:"
    /// section (and any spilled header lines) always stay reachable.</summary>
    public const int MinBodyRows = 3;

    /// <summary>Minimum coloured-header rows kept on top before the remainder spills into the body —
    /// so the header never fully disappears while there is any room for it.</summary>
    public const int MinHeaderRows = 1;

    /// <summary>The computed split: the coloured header's height, the body's top (relative to the
    /// container) and height, and how many trailing header lines the caller should render as plain text
    /// at the top of the scrollable body.</summary>
    public readonly record struct Layout(int HeaderHeight, int BodyY, int BodyHeight, int SpilledHeaderLines);

    /// <summary>
    /// Splits <paramref name="availableHeight"/> rows between a coloured header of
    /// <paramref name="headerLineCount"/> lines and the scrollable body. Never returns negative sizes.
    /// </summary>
    public static Layout Compute(int headerLineCount, int availableHeight)
    {
        headerLineCount = Math.Max(0, headerLineCount);
        availableHeight = Math.Max(0, availableHeight);

        if (headerLineCount == 0)
            return new Layout(0, 0, availableHeight, 0);

        // Everything fits: full coloured header, a blank gap row, body fills the rest.
        if (headerLineCount + GapRows + MinBodyRows <= availableHeight)
        {
            var bodyY = headerLineCount + GapRows;
            return new Layout(headerLineCount, bodyY, availableHeight - bodyY, 0);
        }

        // Constrained: cap the header from the bottom and reserve the body its minimum. The body starts
        // immediately after the header (no separate gap row) — the blank separator before "Custom
        // fields:" is carried in the spilled body text instead.
        var headerHeight = Math.Clamp(availableHeight - MinBodyRows, MinHeaderRows, headerLineCount);
        // On a pathologically tiny window the header alone can exceed the whole area; keep it in bounds.
        if (headerHeight > availableHeight)
            headerHeight = availableHeight;

        var bodyHeight = Math.Max(0, availableHeight - headerHeight);
        var spilled = headerLineCount - headerHeight;
        return new Layout(headerHeight, headerHeight, bodyHeight, spilled);
    }
}
