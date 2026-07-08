namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// Pure navigation/routing logic for the detail view's Dispatch pane (issue #93, D1 of the #90
/// epic), factored out of the Terminal.Gui glue so it's unit-testable without a terminal — the same
/// pure-glue split as <see cref="StatusPickerModel"/>. It decides which action a key maps to while
/// the pane is open, how focus cycles between the pane's controls, and how tall the pane should be.
/// </summary>
public static class DispatchPaneModel
{
    /// <summary>
    /// The keys the pane intercepts. The glue classifies a Terminal.Gui <c>Key</c> into one of these;
    /// anything else is <see cref="PaneKey.Other"/> and falls through to the focused control (so
    /// typing into a text field and Space-toggling a check box keep working).
    /// </summary>
    public enum PaneKey
    {
        Enter,
        Escape,
        Tab,
        BackTab,
        PageUp,
        PageDown,
        Other,
    }

    /// <summary>What the glue should do for a classified key.</summary>
    public enum PaneAction
    {
        Submit,
        Cancel,
        FocusNext,
        FocusPrevious,
        ScrollUnderlyingPageUp,
        ScrollUnderlyingPageDown,
        PassThrough,
    }

    /// <summary>
    /// Maps a key to its action. The decision is independent of which control has focus: Enter always
    /// submits the pane, Esc cancels, Tab/Shift+Tab move focus between the pane's controls, and
    /// PgUp/PgDn scroll the tab body *above* the pane (they're routed through, not trapped) so the
    /// user can review Description/Comments while composing.
    /// </summary>
    public static PaneAction Route(PaneKey key) => key switch
    {
        PaneKey.Enter => PaneAction.Submit,
        PaneKey.Escape => PaneAction.Cancel,
        PaneKey.Tab => PaneAction.FocusNext,
        PaneKey.BackTab => PaneAction.FocusPrevious,
        PaneKey.PageUp => PaneAction.ScrollUnderlyingPageUp,
        PaneKey.PageDown => PaneAction.ScrollUnderlyingPageDown,
        _ => PaneAction.PassThrough,
    };

    /// <summary>
    /// The next control index when cycling focus with Tab (<paramref name="forward"/>) / Shift+Tab,
    /// wrapping at both ends — the same wraparound the detail-tab cycle uses. Returns 0 for a
    /// non-positive <paramref name="count"/> (nothing to focus).
    /// </summary>
    public static int NextFocus(int current, int count, bool forward)
    {
        if (count <= 0)
            return 0;
        var step = forward ? 1 : -1;
        return ((current + step) % count + count) % count;
    }

    /// <summary>The pane's ideal height: one row per control plus the top+bottom frame border.</summary>
    public static int PreferredHeight(int controlCount) => Math.Max(0, controlCount) + 2;

    /// <summary>
    /// The pane height clamped so at least <paramref name="minTabRows"/> of the tab above stays
    /// visible on short terminals, but never below the 3-row minimum (top border + prompt row +
    /// bottom border) that keeps the prompt on screen. When the terminal is too short to honour both,
    /// the minimum wins and the pane's bottom stub controls clip before the prompt does.
    /// </summary>
    public static int ClampHeight(int preferred, int availableHeight, int minTabRows)
    {
        const int minPane = 3;
        var ceiling = availableHeight - Math.Max(0, minTabRows);
        if (ceiling < minPane)
            ceiling = minPane;
        return Math.Max(minPane, Math.Min(preferred, ceiling));
    }
}
