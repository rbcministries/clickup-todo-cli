using System.Globalization;
using ClickUpTodo.Agent;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui.Screens;

/// <summary>The result of editing settings, or null when the user cancels.</summary>
public sealed record SettingsResult(int RefreshSeconds, int FeedRefreshSeconds, int FeedActivityLookbackDays, string DefaultWorkingDirectory, string WorkspaceSubdomain, AgentDispatchSettings AgentDispatch, DetailViewSettings DetailView, bool ConfirmOnExit);

/// <summary>
/// Carries a prompt-template edit request from the settings screen to the host (#100): the current
/// template plus an <see cref="Apply"/> callback the host invokes with the edited value once the
/// editor screen returns. The settings screen folds the value back into its carried template, so an
/// F2 Save persists it (and an F2 Cancel discards it).
/// </summary>
public sealed record PromptTemplateEditRequest(string CurrentTemplate, Action<string> Apply);

/// <summary>
/// A full-window settings screen. The left column changes the refresh interval; the right column
/// is the consolidated <b>Dispatch</b> section (#27, #101) — preferred terminal, <c>claude</c>
/// executable + extra args, working directory, the per-dispatch-pane defaults (#94 session mode,
/// #97 post-to-Comments) the pane initializes from, and a button to edit the prompt template (#100)
/// on its own screen. Hiding statuses is no longer here — it's a
/// regular F3 filter rule (<c>Status IS NOT …</c>) as of #69. On Save it exposes the new values via
/// <see cref="Result"/> and closes; Cancel/Esc close with <see cref="Result"/> left null. The host
/// reads <see cref="Result"/> in its close handler.
/// </summary>
public sealed class SettingsScreen : Screen
{
    private static readonly PreferredTerminal[] TerminalOrder =
        [PreferredTerminal.Auto, PreferredTerminal.WindowsTerminal, PreferredTerminal.Pwsh, PreferredTerminal.PowerShell, PreferredTerminal.Cmd];

    private static readonly AgentWorkingDirectory[] WorkingDirOrder =
        [AgentWorkingDirectory.TaskDerived, AgentWorkingDirectory.Home, AgentWorkingDirectory.Fixed];

    private readonly TextField _refreshField;
    private readonly TextField _feedRefreshField;
    private readonly TextField _feedLookbackField;

    /// <summary>
    /// The dispatch prompt template (#100), carried through this screen unchanged and edited on the
    /// dedicated editor screen. Kept here so an F2 Save preserves it (rather than resetting it to
    /// blank) and so a returning edit can be folded back in.
    /// </summary>
    private string _promptTemplate;

    /// <summary>The saved settings, or null if the screen was cancelled.</summary>
    public SettingsResult? Result { get; private set; }

    /// <summary>
    /// Raised when the user opens the prompt-template editor (#100). The host shows the editor screen
    /// and applies the returned template back via <see cref="PromptTemplateEditRequest.Apply"/>; the
    /// TUI editor screen itself isn't unit-testable, so this seam keeps the settings screen thin.
    /// </summary>
    public event EventHandler<PromptTemplateEditRequest>? EditPromptTemplateRequested;

    public SettingsScreen(int refreshSeconds, int feedRefreshSeconds, int feedActivityLookbackDays, string defaultWorkingDirectory, string workspaceSubdomain, AgentDispatchSettings dispatch, DetailViewSettings detailView, bool confirmOnExit)
    {
        Title = "Settings";
        _promptTemplate = dispatch.PromptTemplate;

        // Home directory used to expand a leading `~` in the working-dir field on Save.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // ── Left column: refresh intervals ─────────────────────────────────────
        var refreshLabel = new Label { X = 1, Y = 1, Text = "Refresh interval (seconds):" };
        _refreshField = new TextField
        {
            X = Pos.Right(refreshLabel) + 1,
            Y = 1,
            Width = 8,
            Text = refreshSeconds.ToString(CultureInfo.InvariantCulture),
        };

        // The feed (Ctrl+E) polls on its own, longer cadence (#123) — assembling it fans a comment
        // fetch out across every assigned task, so it's far heavier than the task-list poll above.
        var feedRefreshLabel = new Label { X = 1, Y = 2, Text = "Feed refresh (seconds):" };
        _feedRefreshField = new TextField
        {
            X = Pos.Right(feedRefreshLabel) + 1,
            Y = 2,
            Width = 8,
            Text = feedRefreshSeconds.ToString(CultureInfo.InvariantCulture),
        };

        // Optional server-side look-back window for the feed's assigned-task fetch (#244): 0 = off
        // (fetch the full set, today's behaviour); N>0 narrows the feed to tasks updated in the last
        // N days. Because that one fetch feeds both the activity projection and the comments, the field
        // is worded to make the "fetch fewer" trade-off clear.
        var feedLookbackLabel = new Label { X = 1, Y = 3, Text = "Feed look-back (days, 0 = all):" };
        _feedLookbackField = new TextField
        {
            X = Pos.Right(feedLookbackLabel) + 1,
            Y = 3,
            Width = 8,
            Text = feedActivityLookbackDays.ToString(CultureInfo.InvariantCulture),
        };

        // Status hiding moved to F3 filter rules (#69) — point the user there rather than a control here.
        var excludedNote = new Label
        {
            X = 1,
            Y = 4,
            Width = Dim.Percent(48),
            Text = "To hide statuses, add a Status IS NOT rule in the F3 filter view.",
        };

        // Base working directory (#92): a root, distinct from the Agent "Fixed dir" mode.
        // Widths are capped to the left column so long text can't overflow into the right column.
        var workingDirLabel = new Label { X = 1, Y = 5, Width = Dim.Percent(48), Text = "Default working directory:" };
        var workingDirField = new TextField { X = 1, Y = 6, Width = Dim.Percent(48), Text = defaultWorkingDirectory };
        var workingDirNote = new Label
        {
            X = 1,
            Y = 7,
            Width = Dim.Percent(48),
            Text = "Blank = ~/ClickUp-Tasks (≠ Fixed dir).",
        };

        // Workspace subdomain (#304): when set, Ctrl+B rewrites an app.clickup.com task URL onto
        // {subdomain}.clickup.com so the browser skips the app→subdomain redirect. Blank = off (open the
        // URL as ClickUp returns it). Normalized on Save so a pasted host/URL reduces to the bare label.
        var subdomainLabel = new Label { X = 1, Y = 8, Text = "ClickUp subdomain:" };
        var subdomainField = new TextField
        {
            X = Pos.Right(subdomainLabel) + 1,
            Y = 8,
            Width = 14,
            Text = workspaceSubdomain,
        };

        // ── Detail view (#108): default tab, activity order, auto-scroll ────────
        // Cycle buttons mirror the Dispatch section's terminal/working-dir buttons. The activity order is
        // also toggleable on the detail screen (Ctrl+PgUp/PgDn, #106) where it governs both the Stream and
        // Comments tabs; here it sets the default.
        var detailHeader = new Label { X = 1, Y = 9, Text = "─ Detail view ─" };

        var defaultTab = detailView.DefaultTab;
        var defaultTabButton = new Button { X = 1, Y = 10, Text = DefaultTabText(defaultTab) };
        defaultTabButton.Accepting += (_, _) =>
        {
            defaultTab = defaultTab.Next();
            defaultTabButton.Text = DefaultTabText(defaultTab);
        };

        var activityOrder = detailView.StreamSort;
        var activityOrderButton = new Button { X = 1, Y = 11, Text = ActivityOrderText(activityOrder) };
        activityOrderButton.Accepting += (_, _) =>
        {
            activityOrder = activityOrder.Next();
            activityOrderButton.Text = ActivityOrderText(activityOrder);
        };

        var autoScroll = detailView.AutoScroll;
        var autoScrollButton = new Button { X = 1, Y = 12, Text = AutoScrollText(autoScroll) };
        autoScrollButton.Accepting += (_, _) =>
        {
            autoScroll = autoScroll.Next();
            autoScrollButton.Text = AutoScrollText(autoScroll);
        };

        // Where a Ctrl+Click on a task link in a detail pane goes (#320): browser or a new terminal tab
        // (Ctrl+Shift inverts). Default Browser matches #318.
        var taskLinkCtrlClick = detailView.TaskLinkCtrlClick;
        var taskLinkCtrlClickButton = new Button { X = 1, Y = 13, Text = TaskLinkCtrlClickText(taskLinkCtrlClick) };
        taskLinkCtrlClickButton.Accepting += (_, _) =>
        {
            taskLinkCtrlClick = taskLinkCtrlClick.Next();
            taskLinkCtrlClickButton.Text = TaskLinkCtrlClickText(taskLinkCtrlClick);
        };

        // What Ctrl+B does to a non-root detail view (#518): keep the view open (default) or close it
        // back to the list / parent. A root view (the --task launch task) always stays regardless — the
        // invariant is structural in the hosts, not a value here.
        var openBrowser = detailView.OpenBrowser;
        var openBrowserButton = new Button { X = 1, Y = 14, Text = OpenBrowserText(openBrowser) };
        openBrowserButton.Accepting += (_, _) =>
        {
            openBrowser = openBrowser.Next();
            openBrowserButton.Text = OpenBrowserText(openBrowser);
        };

        // ── General (#407): opt out of the exit-confirmation modal ──────────────
        // A cycle toggle mirroring the Dispatch section's On/Off buttons. On by default; Off restores the
        // pre-#299 one-key quit. Both hosts' RequestExit read the persisted value live.
        var generalHeader = new Label { X = 1, Y = 15, Text = "─ General ─" };
        var confirmExit = confirmOnExit;
        var confirmOnExitButton = new Button { X = 1, Y = 16, Text = ConfirmOnExitText(confirmExit) };
        confirmOnExitButton.Accepting += (_, _) =>
        {
            confirmExit = !confirmExit;
            confirmOnExitButton.Text = ConfirmOnExitText(confirmExit);
        };

        // ── Right column: Dispatch (#27, consolidated in #101) ──────────────────
        var rightX = Pos.Percent(50) + 1;
        var dispatchHeader = new Label { X = rightX, Y = 0, Text = "─ Dispatch ─" };

        // The exe/args fields edit the resolved default provider (#497). Additional providers configured
        // in config.json are carried through this screen untouched (see the Save block) so an F2 Save
        // never drops them; the full multi-provider editor is the F2 sub-screen (Phase 2 of #497).
        var defaultProvider = dispatch.ResolveDefaultProvider();
        var exeLabel = new Label { X = rightX, Y = 1, Text = "Claude executable (blank = claude):" };
        var exeField = new TextField { X = rightX, Y = 2, Width = Dim.Fill(2), Text = defaultProvider.Executable };

        var argsLabel = new Label { X = rightX, Y = 4, Text = "Extra args (space-separated):" };
        var argsField = new TextField { X = rightX, Y = 5, Width = Dim.Fill(2), Text = SettingsForm.FormatExtraArgs(defaultProvider.ExtraArgs) };

        var terminal = dispatch.PreferredTerminal;
        var terminalButton = new Button { X = rightX, Y = 7, Text = TerminalText(terminal) };
        terminalButton.Accepting += (_, _) =>
        {
            var i = Array.IndexOf(TerminalOrder, terminal);
            terminal = TerminalOrder[(i + 1) % TerminalOrder.Length];
            terminalButton.Text = TerminalText(terminal);
        };

        var workingDir = dispatch.WorkingDirectory;
        var workingDirButton = new Button { X = rightX, Y = 8, Text = WorkingDirText(workingDir) };
        workingDirButton.Accepting += (_, _) =>
        {
            var i = Array.IndexOf(WorkingDirOrder, workingDir);
            workingDir = WorkingDirOrder[(i + 1) % WorkingDirOrder.Length];
            workingDirButton.Text = WorkingDirText(workingDir);
        };

        var fixedDirLabel = new Label { X = rightX, Y = 9, Text = "Fixed dir (when Working dir = Fixed):" };
        var fixedDirField = new TextField { X = rightX, Y = 10, Width = Dim.Fill(2), Text = dispatch.FixedWorkingDirectory };

        // The prompt template (#100) is edited on its own screen; this button opens it via the host.
        var templateButton = new Button { X = rightX, Y = 12, Text = "Edit prompt template…" };
        templateButton.Accepting += (_, _) =>
            EditPromptTemplateRequested?.Invoke(this, new PromptTemplateEditRequest(_promptTemplate, t => _promptTemplate = t));

        // Custom terminal launch command (#385): a user-specified emulator/wrapper tried ahead of the
        // auto-detected chain (covers an emulator not in the probe list, or a macOS/Linux preference).
        // `{}` marks where the launched command is spliced in (appended if omitted). Blank = auto-detect.
        var customTermLabel = new Label { X = rightX, Y = 13, Text = "Custom terminal cmd ({} = command):" };
        var customTermField = new TextField { X = rightX, Y = 14, Width = Dim.Fill(2), Text = dispatch.CustomTerminalCommand };

        // Dispatch-pane defaults (#101): the per-dispatch toggles #94/#97 add to the pane initialize
        // from these. Cycle buttons mirror the terminal/working-dir buttons above.
        var sessionMode = dispatch.DefaultSessionMode;
        var sessionModeButton = new Button { X = rightX, Y = 15, Text = SessionModeText(sessionMode) };
        sessionModeButton.Accepting += (_, _) =>
        {
            sessionMode = sessionMode == AgentSessionMode.Interactive ? AgentSessionMode.OneOff : AgentSessionMode.Interactive;
            sessionModeButton.Text = SessionModeText(sessionMode);
        };

        var postToComments = dispatch.DefaultPostResultsToComments;
        var postToCommentsButton = new Button { X = rightX, Y = 16, Text = PostToCommentsText(postToComments) };
        postToCommentsButton.Accepting += (_, _) =>
        {
            postToComments = !postToComments;
            postToCommentsButton.Text = PostToCommentsText(postToComments);
        };

        // Launch location (#255): where an interactive session opens — a new window (default) or a new
        // tab of the current terminal where the host supports it (best-effort, falls back to a window).
        var launchLocation = dispatch.LaunchLocation;
        var launchLocationButton = new Button { X = rightX, Y = 17, Text = LaunchLocationText(launchLocation) };
        launchLocationButton.Accepting += (_, _) =>
        {
            launchLocation = launchLocation == LaunchLocation.NewWindow ? LaunchLocation.NewTab : LaunchLocation.NewWindow;
            launchLocationButton.Text = LaunchLocationText(launchLocation);
        };

        // Windows-only (#462): on a match, launch under the Windows Terminal profile whose
        // startingDirectory equals the resolved dispatch dir, so the session inherits its
        // appearance/environment. Off by default; a strict no-op off Windows or on no match.
        var tryWtProfiles = dispatch.TryUseWindowsTerminalProfiles;
        var tryWtProfilesButton = new Button { X = rightX, Y = 18, Text = TryWtProfilesText(tryWtProfiles) };
        tryWtProfilesButton.Accepting += (_, _) =>
        {
            tryWtProfiles = !tryWtProfiles;
            tryWtProfilesButton.Text = TryWtProfilesText(tryWtProfiles);
        };

        // Builds the dispatch settings on Save (#497): the exe/args fields edit the resolved default
        // provider via the pure SettingsForm.ApplyDefaultProviderEdit, which preserves the other
        // configured providers and the chosen default name so they survive an F2 round-trip.
        AgentDispatchSettings BuildDispatchSettings()
        {
            var (providers, defaultName) = SettingsForm.ApplyDefaultProviderEdit(
                dispatch.Providers, dispatch.DefaultProviderName, exeField.Text, SettingsForm.ParseExtraArgs(argsField.Text));
            return new AgentDispatchSettings
            {
                PreferredTerminal = terminal,
                CustomTerminalCommand = customTermField.Text?.Trim() ?? "",
                Providers = providers,
                DefaultProviderName = defaultName,
                WorkingDirectory = workingDir,
                FixedWorkingDirectory = fixedDirField.Text?.Trim() ?? "",
                DefaultSessionMode = sessionMode,
                DefaultPostResultsToComments = postToComments,
                LaunchLocation = launchLocation,
                TryUseWindowsTerminalProfiles = tryWtProfiles,
                PromptTemplate = _promptTemplate,
            };
        }

        var save = new Button { X = 1, Y = Pos.AnchorEnd(1), Text = "Save", IsDefault = true };
        var cancel = new Button { X = Pos.Right(save) + 2, Y = Pos.AnchorEnd(1), Text = "Cancel" };
        save.Accepting += (_, _) =>
        {
            Result = new SettingsResult(
                SettingsForm.ParseRefreshSeconds(_refreshField.Text, refreshSeconds),
                SettingsForm.ParseRefreshSeconds(_feedRefreshField.Text, feedRefreshSeconds),
                SettingsForm.ParseLookbackDays(_feedLookbackField.Text, feedActivityLookbackDays),
                SettingsForm.ExpandHomePath(workingDirField.Text, home),
                ClickUpUrl.NormalizeSubdomain(subdomainField.Text),
                BuildDispatchSettings(),
                new DetailViewSettings
                {
                    DefaultTab = defaultTab,
                    StreamSort = activityOrder,
                    AutoScroll = autoScroll,
                    TaskLinkCtrlClick = taskLinkCtrlClick,
                    OpenBrowser = openBrowser,
                },
                confirmExit);
            Close();
        };
        cancel.Accepting += (_, _) => Close();

        // Esc cancels from anywhere on the screen (Result stays null); F1 opens Help (#103).
        KeyDown += (_, key) =>
        {
            switch (key.KeyCode)
            {
                case KeyCode.Esc:
                    key.Handled = true;
                    Close();
                    break;
                case KeyCode.F1:
                    key.Handled = true;
                    RequestHelp();
                    break;
            }
        };

        Add([
            refreshLabel, _refreshField, feedRefreshLabel, _feedRefreshField,
            feedLookbackLabel, _feedLookbackField, excludedNote,
            workingDirLabel, workingDirField, workingDirNote,
            subdomainLabel, subdomainField,
            detailHeader, defaultTabButton, activityOrderButton, autoScrollButton, taskLinkCtrlClickButton,
            openBrowserButton,
            generalHeader, confirmOnExitButton,
            dispatchHeader, exeLabel, exeField, argsLabel, argsField, terminalButton, workingDirButton,
            fixedDirLabel, fixedDirField, templateButton, customTermLabel, customTermField,
            sessionModeButton, postToCommentsButton, launchLocationButton, tryWtProfilesButton,
            save, cancel,
        ]);
    }

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.Settings;

    public override void OnShown() => _refreshField.SetFocus();

    private static string TerminalText(PreferredTerminal t) => "Terminal: " + t switch
    {
        PreferredTerminal.WindowsTerminal => "Windows Terminal",
        PreferredTerminal.Pwsh => "pwsh",
        PreferredTerminal.PowerShell => "Windows PowerShell",
        PreferredTerminal.Cmd => "cmd",
        _ => "Auto",
    };

    private static string WorkingDirText(AgentWorkingDirectory w) => "Working dir: " + w switch
    {
        AgentWorkingDirectory.Home => "Home",
        AgentWorkingDirectory.Fixed => "Fixed",
        _ => "Task-derived",
    };

    private static string SessionModeText(AgentSessionMode m) => "Default session: " + m switch
    {
        AgentSessionMode.OneOff => "One-off",
        _ => "Interactive",
    };

    private static string PostToCommentsText(bool on) => "Default post to Comments: " + (on ? "On" : "Off");

    private static string ConfirmOnExitText(bool on) => "Confirm on exit: " + (on ? "On" : "Off");

    private static string LaunchLocationText(LaunchLocation l) => "Launch: " + l switch
    {
        LaunchLocation.NewTab => "New tab (where supported)",
        _ => "New window",
    };

    private static string TryWtProfilesText(bool on) => "Try WT profiles (Windows): " + (on ? "On" : "Off");

    private static string DefaultTabText(DetailTab t) => "Default tab: " + t switch
    {
        DetailTab.Description => "Description",
        DetailTab.Comments => "Comments",
        DetailTab.Other => "Other",
        DetailTab.Checklists => "Checklists",
        _ => "Stream",
    };

    private static string ActivityOrderText(StreamSort s) => "Activity order: " + s switch
    {
        StreamSort.Descending => "Newest first",
        _ => "Oldest first",
    };

    private static string AutoScrollText(StreamAutoScroll s) => "Auto-scroll: " + s switch
    {
        StreamAutoScroll.Oldest => "Oldest",
        _ => "Newest",
    };

    // Kept short ("New tab", not "New terminal tab") so the widest state stays within the left column at
    // ~80 cols — the right column starts at Pos.Percent(50)+1 (#320 review). README spells out the full
    // "new terminal tab" behaviour.
    private static string TaskLinkCtrlClickText(TaskLinkCtrlClickDestination d) => "Ctrl+Click task link: " + d switch
    {
        TaskLinkCtrlClickDestination.NewTerminalTab => "New tab",
        _ => "Browser",
    };

    // #518: what Ctrl+B does to a non-root detail view — keep it open (default) or close back to the
    // list/parent. A root (--task) view always stays; the invariant lives in the hosts, not here.
    private static string OpenBrowserText(OpenBrowserBehavior b) => "Ctrl+B: " + b switch
    {
        OpenBrowserBehavior.CloseView => "Open browser + close",
        _ => "Open browser, stay",
    };
}
