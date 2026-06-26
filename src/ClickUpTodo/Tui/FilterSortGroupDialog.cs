using System.Collections.ObjectModel;
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

namespace ClickUpTodo.Tui;

/// <summary>
/// A modal editor for the F3 view: build filter rules (field / operator / value), pick a sort field
/// and direction, and pick a group field. Returns the new <see cref="ViewSettings"/> on Save, or
/// null on Cancel. All semantics live in the pure <see cref="TaskView"/> engine — this is only the
/// presentation, kept thin so it can move to a full-window screen later (#38).
/// </summary>
public static class FilterSortGroupDialog
{
    private static readonly TaskField[] Fields =
        [TaskField.Status, TaskField.List, TaskField.LastActivity, TaskField.Due];

    private static readonly FilterOp[] Ops =
        [FilterOp.Is, FilterOp.IsNot, FilterOp.GreaterThan, FilterOp.LessThan, FilterOp.GreaterOrEqual, FilterOp.LessOrEqual];

    public static ViewSettings? Show(ViewSettings current)
    {
        // Work on a copy so Cancel leaves the caller's settings untouched.
        var working = current.Filters.Select(r => r with { }).ToList();
        var direction = current.SortDirection;

        var dialog = new Dialog
        {
            Title = "Filter · Sort · Group",
            Width = Dim.Percent(85),
            Height = Dim.Percent(85),
        };

        // ── Left column: build a filter, then the active-filter list ──────────
        var addHeader = new Label { X = 1, Y = 0, Text = "─ Add a filter ─" };
        var fieldLabel = new Label { X = 1, Y = 1, Text = "Field:" };
        var fieldList = new ListView { X = 1, Y = 2, Width = 26, Height = 4 };
        fieldList.SetSource(new ObservableCollection<string>(Fields.Select(TaskFieldInfo.DisplayName)));
        fieldList.SelectedItem = 0;

        var opLabel = new Label { X = 1, Y = 6, Text = "Operator:" };
        var opList = new ListView { X = 1, Y = 7, Width = 26, Height = 6 };
        opList.SetSource(new ObservableCollection<string>(Ops.Select(TaskFieldInfo.OpSymbol)));
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
        sortList.SetSource(new ObservableCollection<string>(FieldChoices()));
        sortList.SelectedItem = FieldToIndex(current.SortField);

        var dirButton = new Button { X = rightX, Y = 9, Text = DirectionText(direction) };
        dirButton.Accepting += (_, _) =>
        {
            direction = direction == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending;
            dirButton.Text = DirectionText(direction);
        };

        var groupHeader = new Label { X = rightX, Y = 11, Text = "─ Group ─" };
        var groupLabel = new Label { X = rightX, Y = 12, Text = "Group by:" };
        var groupList = new ListView { X = rightX, Y = 13, Width = Dim.Fill(2), Height = 6 };
        groupList.SetSource(new ObservableCollection<string>(FieldChoices()));
        groupList.SelectedItem = FieldToIndex(current.GroupField);

        var hint = new Label
        {
            X = 1,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(1),
            Text = "Tab moves · Enter in Value adds · Del removes selected filter",
        };

        void Add()
        {
            var field = Fields[Clamp(fieldList.SelectedItem, Fields.Length)];
            var op = Ops[Clamp(opList.SelectedItem, Ops.Length)];
            var value = valueField.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(value))
            {
                hint.Text = "Enter a value before adding a filter.";
                return;
            }
            if (!TaskFieldInfo.IsNumeric(field) && op is not (FilterOp.Is or FilterOp.IsNot))
            {
                hint.Text = $"{TaskFieldInfo.DisplayName(field)} only supports IS / IS NOT.";
                return;
            }
            var rule = new FilterRule { Field = field, Op = op, Value = value };
            working.Add(rule);
            filterDisplay.Add(TaskFieldInfo.Describe(rule));
            valueField.Text = "";
            valueField.SetFocus();
        }

        void Remove()
        {
            if (filtersList.SelectedItem is int i && i >= 0 && i < working.Count)
            {
                working.RemoveAt(i);
                filterDisplay.RemoveAt(i);
            }
        }

        addButton.Accepting += (_, _) => Add();
        removeButton.Accepting += (_, _) => Remove();
        valueField.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Enter)
            {
                key.Handled = true;
                Add();
            }
        };
        filtersList.KeyDown += (_, key) =>
        {
            if (key.KeyCode is KeyCode.Delete or KeyCode.Backspace)
            {
                key.Handled = true;
                Remove();
            }
        };

        ViewSettings? result = null;
        var save = new Button { X = 1, Y = Pos.AnchorEnd(1), Text = "Save", IsDefault = true };
        var cancel = new Button { X = Pos.Right(save) + 2, Y = Pos.AnchorEnd(1), Text = "Cancel" };
        var clear = new Button { X = Pos.Right(cancel) + 2, Y = Pos.AnchorEnd(1), Text = "Clear all" };

        save.Accepting += (_, _) =>
        {
            result = new ViewSettings
            {
                Filters = working,
                SortField = IndexToField(sortList.SelectedItem),
                SortDirection = direction,
                GroupField = IndexToField(groupList.SelectedItem),
            };
            Application.RequestStop();
        };
        cancel.Accepting += (_, _) => Application.RequestStop();
        clear.Accepting += (_, _) =>
        {
            working.Clear();
            filterDisplay.Clear();
            sortList.SelectedItem = 0;
            groupList.SelectedItem = 0;
            direction = SortDirection.Ascending;
            dirButton.Text = DirectionText(direction);
        };

        dialog.Add(
            addHeader, fieldLabel, fieldList, opLabel, opList, valueLabel, valueField, addButton, removeButton,
            activeHeader, filtersList,
            sortHeader, sortLabel, sortList, dirButton, groupHeader, groupLabel, groupList,
            hint, save, cancel, clear);

        fieldList.SetFocus();
        Application.Run(dialog);
        dialog.Dispose();
        return result;
    }

    // "(none)" first, then the four fields — shared by the sort and group pickers.
    private static IEnumerable<string> FieldChoices()
        => new[] { "(none)" }.Concat(Fields.Select(TaskFieldInfo.DisplayName));

    private static int FieldToIndex(TaskField? field)
        => field is null ? 0 : Array.IndexOf(Fields, field.Value) + 1;

    private static TaskField? IndexToField(int? selected)
        => selected is int i && i >= 1 && i <= Fields.Length ? Fields[i - 1] : null;

    private static int Clamp(int? selected, int count)
        => selected is int i && i >= 0 && i < count ? i : 0;

    private static string DirectionText(SortDirection direction)
        => $"Direction: {(direction == SortDirection.Ascending ? "Ascending" : "Descending")}";
}
