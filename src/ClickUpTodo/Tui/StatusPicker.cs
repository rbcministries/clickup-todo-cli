using System.Collections.ObjectModel;
using ClickUpTodo.ClickUp;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui;

/// <summary>A modal dialog that lets the user pick a new status from a list's workflow.</summary>
public static class StatusPicker
{
    /// <summary>Shows the picker and returns the chosen status name, or null if cancelled.</summary>
    public static string? Show(string taskName, IReadOnlyList<StatusOption> statuses, string? currentStatus)
    {
        if (statuses.Count == 0)
            return null;

        var title = taskName.Length > 40 ? taskName[..39] + "…" : taskName;
        var dialog = new Dialog
        {
            Title = $"Set status — {title}",
            Width = Dim.Percent(60),
            Height = Dim.Percent(60),
        };

        var list = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };
        list.SetSource(new ObservableCollection<string>(statuses.Select(FormatStatus)));

        // Pre-select the task's current status.
        var currentIndex = statuses.ToList().FindIndex(
            s => string.Equals(s.Name, currentStatus, StringComparison.OrdinalIgnoreCase));
        if (currentIndex >= 0)
            list.SelectedItem = currentIndex;

        var hint = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Text = " ↑/↓ move · Enter select · Esc cancel",
        };

        string? chosen = null;
        list.KeyDown += (_, key) =>
        {
            switch (key.KeyCode)
            {
                case KeyCode.Enter:
                    if (list.SelectedItem is int i && i >= 0 && i < statuses.Count)
                        chosen = statuses[i].Name;
                    key.Handled = true;
                    Application.RequestStop();
                    break;
                case KeyCode.Esc:
                    key.Handled = true;
                    Application.RequestStop();
                    break;
            }
        };

        dialog.Add(list, hint);
        list.SetFocus();
        Application.Run(dialog);
        dialog.Dispose();
        return chosen;
    }

    private static string FormatStatus(StatusOption status) => $"  {status.Name}";
}
