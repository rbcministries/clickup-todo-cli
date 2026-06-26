using System.Collections.ObjectModel;
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

/// <summary>
/// A full-window screen to edit the F3 view: build filter rules (field / operator / value), pick a
/// sort field + direction, and a group field. On Save it exposes the new <see cref="ViewSettings"/>
/// via <see cref="Result"/> and closes; Cancel/Esc close with <see cref="Result"/> left null. All
/// semantics live in the pure <see cref="TaskView"/> engine and <see cref="FilterSortGroupForm"/> —
/// this is only presentation, swapped into the dashboard's single toplevel like the other screens (#38).
/// </summary>
public sealed class FilterSortGroupScreen : Screen
{
    private readonly ListView _fieldList;

    /// <summary>The saved view, or null if the screen was cancelled.</summary>
    public ViewSettings? Result { get; private set; }

    public FilterSortGroupScreen(ViewSettings current)
    {
        Title = "Filter · Sort · Group";

        // Work on a copy so Cancel leaves the caller's settings untouched.
        var working = current.Filters.Select(r => r with { }).ToList();
        var direction = current.SortDirection;

        // ── Left column: build a filter, then the active-filter list ──────────
        var addHeader = new Label { X = 1, Y = 0, Text = "─ Add a filter ─" };
        var fieldLabel = new Label { X = 1, Y = 1, Text = "Field:" };
        _fieldList = new ListView { X = 1, Y = 2, Width = 26, Height = 4 };
        _fieldList.SetSource(new ObservableCollection<string>(FilterSortGroupForm.Fields.Select(TaskFieldInfo.DisplayName)));
        _fieldList.SelectedItem = 0;

        var opLabel = new Label { X = 1, Y = 6, Text = "Operator:" };
        var opList = new ListView { X = 1, Y = 7, Width = 26, Height = 6 };
        opList.SetSource(new ObservableCollection<string>(FilterSortGroupForm.Ops.Select(TaskFieldInfo.OpSymbol)));
        opList.SelectedItem = 0;

        var valueLabel = new Label { X = 1, Y = 13, Text = "Value (name, or yyyy-mm-dd):" };
        var valueField = new TextField { X = 1, Y = 14, Width = 26 };

        var addButton = new Button { X = 1, Y = 15, Text = "Add filter" };
        var removeButton = new Button { X = Pos.Right(addButton) + 1, Y = 15, Text = "Remove" };

        var activeHeader = new Label { X = 1, Y = 17, Text = "─ Active filters (ANDed) ─" };
        var filterDisplay = new ObservableCollection<string>(working.Select(TaskFieldInfo.Describe));
        var filtersList = new ListView { X = 1, Y = 18, Width = Dim.Percent(46), Height = Dim.Fill(3) };
        filtersList.SetSource(filterDisplay);

        // ── Right column: sort + group ────────────────────────────────────────
        var rightX = Pos.Percent(50) + 1;
        var sortHeader = new Label { X = rightX, Y = 0, Text = "─ Sort ─" };
        var sortLabel = new Label { X = rightX, Y = 1, Text = "Sort by:" };
        var sortList = new ListView { X = rightX, Y = 2, Width = Dim.Fill(2), Height = 6 };
        sortList.SetSource(new ObservableCollection<string>(FilterSortGroupForm.FieldChoices()));
        sortList.SelectedItem = FilterSortGroupForm.FieldToIndex(current.SortField);

        var dirButton = new Button { X = rightX, Y = 9, Text = DirectionText(direction) };
        dirButton.Accepting += (_, _) =>
        {
            direction = direction == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending;
            dirButton.Text = DirectionText(direction);
        };

        var groupHeader = new Label { X = rightX, Y = 11, Text = "─ Group ─" };
        var groupLabel = new Label { X = rightX, Y = 12, Text = "Group by:" };
        var groupList = new ListView { X = rightX, Y = 13, Width = Dim.Fill(2), Height = 6 };
        groupList.SetSource(new ObservableCollection<string>(FilterSortGroupForm.FieldChoices()));
        groupList.SelectedItem = FilterSortGroupForm.FieldToIndex(current.GroupField);

        var hint = new Label
        {
            X = 1,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(1),
            Text = "Tab moves · Enter in Value adds · Del removes selected filter · Esc cancels",
        };

        void AddFilter()
        {
            var field = FilterSortGroupForm.Fields[FilterSortGroupForm.Clamp(_fieldList.SelectedItem, FilterSortGroupForm.Fields.Count)];
            var op = FilterSortGroupForm.Ops[FilterSortGroupForm.Clamp(opList.SelectedItem, FilterSortGroupForm.Ops.Count)];
            if (!FilterSortGroupForm.TryBuildRule(field, op, valueField.Text, out var rule, out var error))
            {
                hint.Text = error!;
                return;
            }
            working.Add(rule!);
            filterDisplay.Add(TaskFieldInfo.Describe(rule!));
            valueField.Text = "";
            valueField.SetFocus();
        }

        void RemoveFilter()
        {
            if (filtersList.SelectedItem is int i && i >= 0 && i < working.Count)
            {
                working.RemoveAt(i);
                filterDisplay.RemoveAt(i);
            }
        }

        addButton.Accepting += (_, _) => AddFilter();
        removeButton.Accepting += (_, _) => RemoveFilter();
        valueField.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Enter)
            {
                key.Handled = true;
                AddFilter();
            }
        };
        filtersList.KeyDown += (_, key) =>
        {
            if (key.KeyCode is KeyCode.Delete or KeyCode.Backspace)
            {
                key.Handled = true;
                RemoveFilter();
            }
        };

        var save = new Button { X = 1, Y = Pos.AnchorEnd(1), Text = "Save", IsDefault = true };
        var cancel = new Button { X = Pos.Right(save) + 2, Y = Pos.AnchorEnd(1), Text = "Cancel" };
        var clear = new Button { X = Pos.Right(cancel) + 2, Y = Pos.AnchorEnd(1), Text = "Clear all" };

        save.Accepting += (_, _) =>
        {
            Result = new ViewSettings
            {
                Filters = working,
                SortField = FilterSortGroupForm.IndexToField(sortList.SelectedItem),
                SortDirection = direction,
                GroupField = FilterSortGroupForm.IndexToField(groupList.SelectedItem),
            };
            Close();
        };
        cancel.Accepting += (_, _) => Close();
        clear.Accepting += (_, _) =>
        {
            working.Clear();
            filterDisplay.Clear();
            sortList.SelectedItem = 0;
            groupList.SelectedItem = 0;
            direction = SortDirection.Ascending;
            dirButton.Text = DirectionText(direction);
        };

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
            addHeader, fieldLabel, _fieldList, opLabel, opList, valueLabel, valueField, addButton, removeButton,
            activeHeader, filtersList,
            sortHeader, sortLabel, sortList, dirButton, groupHeader, groupLabel, groupList,
            hint, save, cancel, clear,
        ]);
    }

    public override void OnShown() => _fieldList.SetFocus();

    private static string DirectionText(SortDirection direction)
        => $"Direction: {(direction == SortDirection.Ascending ? "Ascending" : "Descending")}";
}
