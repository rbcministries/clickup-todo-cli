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
public sealed record SettingsResult(int RefreshSeconds, AgentDispatchSettings AgentDispatch);

/// <summary>
/// A full-window settings screen. The left column changes the refresh interval; the right column
/// configures agent dispatch (#27) — preferred terminal, <c>claude</c> executable + extra args,
/// working directory, and a prompt-preamble override. Hiding statuses is no longer here — it's a
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

    public SettingsScreen(int refreshSeconds, AgentDispatchSettings dispatch)
    {
        Title = "Settings";

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

        // ── Right column: agent dispatch (#27) ─────────────────────────────────
        var rightX = Pos.Percent(50) + 1;
        var agentHeader = new Label { X = rightX, Y = 0, Text = "─ Agent dispatch (A) ─" };

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

        var hint = new Label
        {
            X = 1,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(1),
            Text = "Tab moves · Space cycles buttons · Esc cancels",
        };

        var save = new Button { X = 1, Y = Pos.AnchorEnd(1), Text = "Save", IsDefault = true };
        var cancel = new Button { X = Pos.Right(save) + 2, Y = Pos.AnchorEnd(1), Text = "Cancel" };
        save.Accepting += (_, _) =>
        {
            Result = new SettingsResult(
                SettingsForm.ParseRefreshSeconds(_refreshField.Text, refreshSeconds),
                new AgentDispatchSettings
                {
                    PreferredTerminal = terminal,
                    ClaudeExecutable = string.IsNullOrWhiteSpace(exeField.Text) ? "claude" : exeField.Text!.Trim(),
                    ExtraArgs = SettingsForm.ParseExtraArgs(argsField.Text),
                    WorkingDirectory = workingDir,
                    FixedWorkingDirectory = fixedDirField.Text?.Trim() ?? "",
                    PromptPreamble = preambleField.Text?.Trim() ?? "",
                });
            Close();
        };
        cancel.Accepting += (_, _) => Close();

        // Esc cancels from anywhere on the screen (Result stays null).
        KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Esc)
            {
                key.Handled = true;
                Close();
            }
        };

        Add([
            refreshLabel, _refreshField, excludedNote,
            agentHeader, exeLabel, exeField, argsLabel, argsField, terminalButton, workingDirButton,
            fixedDirLabel, fixedDirField, preambleLabel, preambleField,
            hint, save, cancel,
        ]);
    }

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
}
