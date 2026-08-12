using ClickUpTodo.Agent;
using ClickUpTodo.Configuration;

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
    /// Whether the per-dispatch launch-location choice (#275) is meaningful for
    /// <paramref name="sessionMode"/>. Only an <see cref="AgentSessionMode.Interactive"/> session
    /// opens a terminal, so new-window-vs-new-tab applies there; a one-off <c>claude -p</c> run (#94)
    /// goes through the background runner with no terminal, so the pane greys the toggle out — and any
    /// value it happens to carry is ignored downstream. Pure so the enable/disable rule is unit-tested
    /// rather than buried in the CI-untestable glue.
    /// </summary>
    public static bool LaunchLocationApplies(AgentSessionMode sessionMode)
        => sessionMode == AgentSessionMode.Interactive;

    /// <summary>
    /// The next <see cref="LaunchLocation"/> when the launch-destination control is advanced one step
    /// (#508): <see cref="LaunchLocation.NewWindow"/> → <see cref="LaunchLocation.NewTab"/> →
    /// <see cref="LaunchLocation.SplitPane"/> → back to <see cref="LaunchLocation.NewWindow"/>. Shared by
    /// the Settings "Launch:" cycle button and the per-dispatch pane's cycle button so the order and its
    /// wraparound are single-sourced and unit-tested, rather than duplicated across two glue call sites.
    /// A three-way cycle is why the pane's old two-state check box (#275) had to change shape: a check box
    /// can't express the third value.
    /// </summary>
    public static LaunchLocation CycleLaunchLocation(LaunchLocation current) => current switch
    {
        LaunchLocation.NewWindow => LaunchLocation.NewTab,
        LaunchLocation.NewTab => LaunchLocation.SplitPane,
        _ => LaunchLocation.NewWindow,
    };

    /// <summary>
    /// The short human label for a <see cref="LaunchLocation"/> shown on the launch-destination controls
    /// (#255/#508). Pure so both the Settings button and the Dispatch pane button render the same text and
    /// it's asserted in one place. The in-place destinations note "(where supported)" — they're best-effort
    /// and degrade down the split → tab → window ladder on a host that can't honour them.
    /// </summary>
    public static string LaunchLocationLabel(LaunchLocation location) => location switch
    {
        LaunchLocation.NewTab => "New tab (where supported)",
        LaunchLocation.SplitPane => "Split pane (where supported)",
        _ => "New window",
    };

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
    /// The pane's ideal height once the working-dir file-tree browser (#95) is present: the
    /// single-row controls and hint line above the browser (<paramref name="rowsAboveBrowser"/>), the
    /// browser's own rows (<paramref name="browserRows"/>, at least one), the single-row controls
    /// below it (<paramref name="rowsBelowBrowser"/>), plus the top+bottom frame border. Factored out
    /// so the (CI-untestable) glue's sizing is unit-tested.
    /// </summary>
    public static int PreferredHeightWithBrowser(int rowsAboveBrowser, int browserRows, int rowsBelowBrowser)
        => Math.Max(0, rowsAboveBrowser) + Math.Max(1, browserRows) + Math.Max(0, rowsBelowBrowser) + 2;

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
