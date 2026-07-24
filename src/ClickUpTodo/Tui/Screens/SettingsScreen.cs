using System.Globalization;
using ClickUpTodo.Agent;
using ClickUpTodo.Configuration;
using ClickUpTodo.Services;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui.Screens;

/// <summary>The result of editing settings, or null when the user cancels.</summary>
public sealed record SettingsResult(int RefreshSeconds, int FeedRefreshSeconds, int FeedActivityLookbackDays, string DefaultWorkingDirectory, string WorkspaceSubdomain, AgentDispatchSettings AgentDispatch, DetailViewSettings DetailView);

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

    /// <param name="detectSubdomain">
    /// Optional best-effort workspace-subdomain detector (#351). When supplied, a "Detect" button next to
    /// the subdomain field runs it off the UI thread and fills the field on a non-empty result. Null (the
    /// default) hides the button — keeping this screen constructible in tests without a network seam.
    /// </param>
    public SettingsScreen(int refreshSeconds, int feedRefreshSeconds, int feedActivityLookbackDays, string defaultWorkingDirectory, string workspaceSubdomain, AgentDispatchSettings dispatch, DetailViewSettings detailView, Func<CancellationToken, Task<string>>? detectSubdomain = null)
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
            // Narrow enough that the adjacent Detect button stays inside the left column (which the other
            // rows cap at Dim.Percent(48)) rather than overlapping the right column at ~80-col widths.
            Width = 10,
            Text = workspaceSubdomain,
        };

        // Opt-in auto-detect (#351): a best-effort probe fills the field from a redirect to the workspace
        // host, so the user needn't type it. Only shown when a detector is wired (prod: SubdomainProbe;
        // tests/E2E: an injected stub). It runs off the UI thread — the app's Task.Run → Application.Invoke
        // pattern — and fails soft: a blank result (no redirect / network error) leaves the field untouched
        // and flashes an outcome so the manual value stays authoritative. One more control on the existing
        // modal — no second focusable pane (#3), no new bare-letter shortcut (#12).
        Button? detectButton = null;
        if (detectSubdomain is not null)
        {
            var detect = new Button { X = Pos.Right(subdomainField) + 1, Y = 8, Text = "Detect" };
            // A probe can outlive the screen (user Esc'd before it returned): the field/button are then
            // disposed and touching them from the marshalled callback would throw on the UI loop. Cancel the
            // in-flight probe on close, and track disposal so the callback bails — Terminal.Gui exposes no
            // public IsDisposed to poll.
            var detectCts = new CancellationTokenSource();
            var disposed = false;
            detect.Disposing += (_, _) =>
            {
                disposed = true;
                detectCts.Cancel();
            };
            detect.Accepting += (_, e) =>
            {
                e.Handled = true;
                detect.Enabled = false;
                detect.Text = "Detecting…";
                _ = Task.Run(async () =>
                {
                    string found;
                    try
                    {
                        found = await detectSubdomain(detectCts.Token);
                    }
                    catch
                    {
                        found = "";
                    }
                    Application.Invoke(() =>
                    {
                        if (disposed)
                            return;
                        // Button label stays fixed (stable width); the outcome is a transient flash.
                        detect.Enabled = true;
                        detect.Text = "Detect";
                        if (string.IsNullOrEmpty(found))
                        {
                            RequestFlash("No workspace subdomain detected — enter it manually.");
                            return;
                        }
                        subdomainField.Text = found;
                        RequestFlash($"Detected workspace subdomain: {found}");
                    });
                });
            };
            detectButton = detect;
        }

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

        // ── Right column: Dispatch (#27, consolidated in #101) ──────────────────
        var rightX = Pos.Percent(50) + 1;
        var dispatchHeader = new Label { X = rightX, Y = 0, Text = "─ Dispatch ─" };

        var exeLabel = new Label { X = rightX, Y = 1, Text = "Claude executable (blank = claude):" };
        var exeField = new TextField { X = rightX, Y = 2, Width = Dim.Fill(2), Text = dispatch.ClaudeExecutable };

        var argsLabel = new Label { X = rightX, Y = 4, Text = "Extra args (space-separated):" };
        var argsField = new TextField { X = rightX, Y = 5, Width = Dim.Fill(2), Text = SettingsForm.FormatExtraArgs(dispatch.ExtraArgs) };

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
                new AgentDispatchSettings
                {
                    PreferredTerminal = terminal,
                    ClaudeExecutable = string.IsNullOrWhiteSpace(exeField.Text) ? "claude" : exeField.Text!.Trim(),
                    ExtraArgs = SettingsForm.ParseExtraArgs(argsField.Text),
                    WorkingDirectory = workingDir,
                    FixedWorkingDirectory = fixedDirField.Text?.Trim() ?? "",
                    DefaultSessionMode = sessionMode,
                    DefaultPostResultsToComments = postToComments,
                    LaunchLocation = launchLocation,
                    PromptTemplate = _promptTemplate,
                },
                new DetailViewSettings
                {
                    DefaultTab = defaultTab,
                    StreamSort = activityOrder,
                    AutoScroll = autoScroll,
                });
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

        var views = new List<View>
        {
            refreshLabel, _refreshField, feedRefreshLabel, _feedRefreshField,
            feedLookbackLabel, _feedLookbackField, excludedNote,
            workingDirLabel, workingDirField, workingDirNote,
            subdomainLabel, subdomainField,
            detailHeader, defaultTabButton, activityOrderButton, autoScrollButton,
            dispatchHeader, exeLabel, exeField, argsLabel, argsField, terminalButton, workingDirButton,
            fixedDirLabel, fixedDirField, templateButton,
            sessionModeButton, postToCommentsButton, launchLocationButton,
            save, cancel,
        };

        // Place Detect right after the subdomain field (when wired) so tab order matches its visual
        // position instead of trailing after Save/Cancel (#351).
        if (detectButton is not null)
            views.Insert(views.IndexOf(subdomainField) + 1, detectButton);

        Add(views.ToArray());
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

    private static string LaunchLocationText(LaunchLocation l) => "Launch: " + l switch
    {
        LaunchLocation.NewTab => "New tab (where supported)",
        _ => "New window",
    };

    private static string DefaultTabText(DetailTab t) => "Default tab: " + t switch
    {
        DetailTab.Description => "Description",
        DetailTab.Comments => "Comments",
        DetailTab.Other => "Other",
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
}
