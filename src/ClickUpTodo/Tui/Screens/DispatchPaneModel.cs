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

    /// <summary>A per-dispatch option the pane offers, for the provider-applicability rule (#498).</summary>
    public enum DispatchOption
    {
        /// <summary>The working-directory field + file-tree browser (#95).</summary>
        WorkingDirectory,

        /// <summary>The one-off/interactive session-mode toggle (#94).</summary>
        SessionMode,

        /// <summary>The new-window/new-tab launch-location toggle (#275).</summary>
        LaunchLocation,
    }

    /// <summary>
    /// Whether a pane <paramref name="option"/> is meaningful for a provider of the given
    /// <paramref name="kind"/> (#498) — the generalization of <see cref="LaunchLocationApplies"/> from
    /// the one launch-location rule to a per-provider "which options apply" predicate. Every option the
    /// pane offers today (working directory, session mode, launch location) needs a <b>local terminal
    /// process</b> to be meaningful, and only a <see cref="DispatchProviderKind.LocalCli"/> provider has
    /// one; a future non-local kind (a hosted agent, epic #491) supports none of them, so the pane greys
    /// those controls out and any value they carry is ignored downstream — the same contract
    /// <see cref="LaunchLocationApplies"/> established for one-off runs. <paramref name="option"/> stays
    /// in the signature so a later kind that supports <em>some</em> options can refine this per option
    /// rather than all-or-nothing. Pure so the enable/disable rule is unit-tested rather than buried in
    /// the CI-untestable glue.
    /// </summary>
    public static bool DispatchOptionApplies(DispatchProviderKind kind, DispatchOption option)
    {
        var needsLocalProcess = option is DispatchOption.WorkingDirectory
            or DispatchOption.SessionMode or DispatchOption.LaunchLocation;
        return !needsLocalProcess || kind == DispatchProviderKind.LocalCli;
    }

    /// <summary>
    /// Whether the pane shows its provider-selector control (#498): only when there are <b>two or more</b>
    /// configured providers, i.e. an actual choice to make. With zero or one provider there is nothing to
    /// pick, so the row is omitted and the pane renders byte-identically to the pre-#498 layout (the
    /// zero-config invariant). Pure so the show/hide rule is unit-tested.
    /// </summary>
    public static bool ProviderRowVisible(int providerCount) => providerCount >= 2;

    /// <summary>
    /// The row the provider selector opens on (#498): the <paramref name="lastUsedIndex"/> remembered
    /// pick when it is a valid row, else the configured <paramref name="defaultIndex"/> when valid, else
    /// the first row. <paramref name="count"/> is the number of providers; a non-positive count returns 0
    /// (nothing to select). Callers resolve the two indices by locating the last-used / default provider
    /// name in the list (or -1 when absent), so a deleted/renamed remembered provider cleanly falls back
    /// to the default. Pure so the seed rule is unit-tested rather than inlined in the glue.
    /// </summary>
    public static int InitialProviderIndex(int count, int lastUsedIndex, int defaultIndex)
    {
        if (count <= 0)
            return 0;
        if (lastUsedIndex >= 0 && lastUsedIndex < count)
            return lastUsedIndex;
        if (defaultIndex >= 0 && defaultIndex < count)
            return defaultIndex;
        return 0;
    }

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
