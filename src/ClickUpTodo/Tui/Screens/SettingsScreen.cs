using System.Globalization;
using ClickUpTodo.Agent;
using ClickUpTodo.Configuration;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui.Screens;

/// <summary>The result of editing settings, or null when the user cancels.</summary>
public sealed record SettingsResult(int RefreshSeconds, string DefaultWorkingDirectory, AgentDispatchSettings AgentDispatch);

/// <summary>
/// A full-window settings screen. The left column changes the refresh interval; the right column
/// is the consolidated <b>Dispatch</b> section (#101) — preferred terminal, <c>claude</c> executable
/// + extra args, working directory, a prompt-preamble override, and the per-dispatch-pane defaults
/// (#94 session mode, #97 post-to-Comments) the pane initializes from. Hiding statuses is no longer here — it's a
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

    /// <summary>The saved settings, or null if the screen was cancelled.</summary>
    public SettingsResult? Result { get; private set; }

    public SettingsScreen(int refreshSeconds, string defaultWorkingDirectory, AgentDispatchSettings dispatch)
    {
        Title = "Settings";

        // Home directory used to expand a leading `~` in the working-dir field on Save.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // ── Left column: refresh interval ──────────────────────────────────────
        var refreshLabel = new Label { X = 1, Y = 1, Text = "Refresh interval (seconds):" };
        _refreshField = new TextField
        {
            X = Pos.Right(refreshLabel) + 1,
            Y = 1,
            Width = 8,
            Text = refreshSeconds.ToString(CultureInfo.InvariantCulture),
        };

        // Status hiding moved to F3 filter rules (#69) — point the user there rather than a control here.
        var excludedNote = new Label
        {
            X = 1,
            Y = 3,
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

        var preambleLabel = new Label { X = rightX, Y = 12, Text = "Prompt preamble (blank = default):" };
        var preambleField = new TextField { X = rightX, Y = 13, Width = Dim.Fill(2), Text = dispatch.PromptPreamble };

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

        var save = new Button { X = 1, Y = Pos.AnchorEnd(1), Text = "Save", IsDefault = true };
        var cancel = new Button { X = Pos.Right(save) + 2, Y = Pos.AnchorEnd(1), Text = "Cancel" };
        save.Accepting += (_, _) =>
        {
            Result = new SettingsResult(
                SettingsForm.ParseRefreshSeconds(_refreshField.Text, refreshSeconds),
                SettingsForm.ExpandHomePath(workingDirField.Text, home),
                new AgentDispatchSettings
                {
                    PreferredTerminal = terminal,
                    ClaudeExecutable = string.IsNullOrWhiteSpace(exeField.Text) ? "claude" : exeField.Text!.Trim(),
                    ExtraArgs = SettingsForm.ParseExtraArgs(argsField.Text),
                    WorkingDirectory = workingDir,
                    FixedWorkingDirectory = fixedDirField.Text?.Trim() ?? "",
                    DefaultSessionMode = sessionMode,
                    DefaultPostResultsToComments = postToComments,
                    PromptPreamble = preambleField.Text?.Trim() ?? "",
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

        Add([
            refreshLabel, _refreshField, excludedNote,
            workingDirLabel, workingDirField, workingDirNote,
            dispatchHeader, exeLabel, exeField, argsLabel, argsField, terminalButton, workingDirButton,
            fixedDirLabel, fixedDirField, preambleLabel, preambleField,
            sessionModeButton, postToCommentsButton,
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
}
