using System.Collections.ObjectModel;
using System.Globalization;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui;

/// <summary>The result of editing settings, or null when the user cancels.</summary>
public sealed record SettingsResult(int RefreshSeconds, List<string> ExcludedStatuses);

/// <summary>
/// A modal settings editor: change the refresh interval and manage the list of excluded statuses.
/// Returns the new values on Save, or null on Cancel.
/// </summary>
public static class SettingsDialog
{
    public static SettingsResult? Show(int refreshSeconds, IReadOnlyList<string> excludedStatuses)
    {
        var dialog = new Dialog
        {
            Title = "Settings",
            Width = Dim.Percent(70),
            Height = Dim.Percent(75),
        };

        var refreshLabel = new Label { X = 1, Y = 1, Text = "Refresh interval (seconds):" };
        var refreshField = new TextField
        {
            X = Pos.Right(refreshLabel) + 1,
            Y = 1,
            Width = 8,
            Text = refreshSeconds.ToString(CultureInfo.InvariantCulture),
        };

        var excludedLabel = new Label { X = 1, Y = 3, Text = "Excluded statuses (tasks in these are hidden):" };
        var statuses = new ObservableCollection<string>(excludedStatuses);
        var statusList = new ListView
        {
            X = 1,
            Y = 4,
            Width = Dim.Fill(1),
            Height = Dim.Fill(6),
        };
        statusList.SetSource(statuses);

        var addField = new TextField { X = 1, Y = Pos.Bottom(statusList), Width = Dim.Fill(20) };
        var addButton = new Button { X = Pos.Right(addField) + 1, Y = Pos.Bottom(statusList), Text = "Add" };
        var removeButton = new Button { X = Pos.Right(addButton) + 1, Y = Pos.Bottom(statusList), Text = "Remove" };

        void Add()
        {
            var text = addField.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text) &&
                !statuses.Any(s => string.Equals(s, text, StringComparison.OrdinalIgnoreCase)))
            {
                statuses.Add(text);
            }
            addField.Text = "";
            addField.SetFocus();
        }

        void Remove()
        {
            if (statusList.SelectedItem is int i && i >= 0 && i < statuses.Count)
                statuses.RemoveAt(i);
        }

        addButton.Accepting += (_, _) => Add();
        removeButton.Accepting += (_, _) => Remove();

        // Enter in the add field adds; Delete in the list removes the selected status.
        addField.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Enter)
            {
                key.Handled = true;
                Add();
            }
        };
        statusList.KeyDown += (_, key) =>
        {
            if (key.KeyCode is KeyCode.Delete or KeyCode.Backspace)
            {
                key.Handled = true;
                Remove();
            }
        };

        var hint = new Label
        {
            X = 1,
            Y = Pos.Bottom(addField),
            Width = Dim.Fill(1),
            Text = "Tab moves · Enter in box adds · Del removes selected",
        };

        SettingsResult? result = null;
        var save = new Button { X = 1, Y = Pos.AnchorEnd(1), Text = "Save", IsDefault = true };
        var cancel = new Button { X = Pos.Right(save) + 2, Y = Pos.AnchorEnd(1), Text = "Cancel" };
        save.Accepting += (_, _) =>
        {
            var seconds = int.TryParse(refreshField.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
                ? Math.Clamp(s, 10, 3600)
                : refreshSeconds;
            result = new SettingsResult(seconds, [.. statuses]);
            Application.RequestStop();
        };
        cancel.Accepting += (_, _) => Application.RequestStop();

        dialog.Add(refreshLabel, refreshField, excludedLabel, statusList, addField, addButton, removeButton, hint, save, cancel);
        refreshField.SetFocus();
        Application.Run(dialog);
        dialog.Dispose();
        return result;
    }
}
