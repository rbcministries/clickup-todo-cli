using System.Collections.ObjectModel;
using System.Globalization;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui.Screens;

/// <summary>The result of editing settings, or null when the user cancels.</summary>
public sealed record SettingsResult(int RefreshSeconds, List<string> ExcludedStatuses);

/// <summary>
/// A full-window settings screen: change the refresh interval and manage the list of excluded
/// statuses. On Save it exposes the new values via <see cref="Result"/> and closes; Cancel/Esc close
/// with <see cref="Result"/> left null. The host reads <see cref="Result"/> in its close handler.
/// </summary>
public sealed class SettingsScreen : Screen
{
    private readonly TextField _refreshField;

    /// <summary>The saved settings, or null if the screen was cancelled.</summary>
    public SettingsResult? Result { get; private set; }

    public SettingsScreen(int refreshSeconds, IReadOnlyList<string> excludedStatuses)
    {
        Title = "Settings";

        var refreshLabel = new Label { X = 1, Y = 1, Text = "Refresh interval (seconds):" };
        _refreshField = new TextField
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

        // Named to avoid shadowing the inherited View.Add when called unqualified below.
        void AddStatus()
        {
            var text = addField.Text?.Trim();
            if (SettingsForm.CanAdd(statuses, text))
                statuses.Add(text!);
            addField.Text = "";
            addField.SetFocus();
        }

        void RemoveStatus()
        {
            if (statusList.SelectedItem is int i && i >= 0 && i < statuses.Count)
                statuses.RemoveAt(i);
        }

        addButton.Accepting += (_, _) => AddStatus();
        removeButton.Accepting += (_, _) => RemoveStatus();

        // Enter in the add field adds; Delete in the list removes the selected status.
        addField.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Enter)
            {
                key.Handled = true;
                AddStatus();
            }
        };
        statusList.KeyDown += (_, key) =>
        {
            if (key.KeyCode is KeyCode.Delete or KeyCode.Backspace)
            {
                key.Handled = true;
                RemoveStatus();
            }
        };

        var hint = new Label
        {
            X = 1,
            Y = Pos.Bottom(addField),
            Width = Dim.Fill(1),
            Text = "Tab moves · Enter in box adds · Del removes selected · Esc cancels",
        };

        var save = new Button { X = 1, Y = Pos.AnchorEnd(1), Text = "Save", IsDefault = true };
        var cancel = new Button { X = Pos.Right(save) + 2, Y = Pos.AnchorEnd(1), Text = "Cancel" };
        save.Accepting += (_, _) =>
        {
            Result = new SettingsResult(SettingsForm.ParseRefreshSeconds(_refreshField.Text, refreshSeconds), [.. statuses]);
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

        Add([refreshLabel, _refreshField, excludedLabel, statusList, addField, addButton, removeButton, hint, save, cancel]);
    }

    public override void OnShown() => _refreshField.SetFocus();
}
