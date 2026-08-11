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
/// The handle a <see cref="FilterSortGroupFormBuilder"/> hands back: the built controls to mount, the
/// control to focus first, and the marshalled <see cref="ViewSettings"/> result (set on Save, left null
/// on Cancel/Esc). Lets the two hosts — the <c>_screens</c> <see cref="FilterSortGroupScreen"/> and the
/// #554 native-modal <c>Dialog</c> — share the identical form so an A/B differs only in the hosting
/// mechanism (mirroring how #404 shared <see cref="HelpScreen.ShortcutsText"/> between its two hosts).
/// </summary>
public sealed class FilterSortGroupFormHandle
{
    /// <summary>The form controls to add to the host surface, in tab/paint order.</summary>
    public IReadOnlyList<View> Controls { get; internal set; } = [];

    /// <summary>The control the host should focus once mounted (the field picker).</summary>
    public View PrimaryFocus { get; internal set; } = default!;

    /// <summary>The saved view, or null if the form was cancelled.</summary>
    public ViewSettings? Result { get; internal set; }
}

/// <summary>
/// Builds the Filter · Sort · Group form — field/operator/value pickers, the active-filter list, the
/// sort/direction/group pickers, and the Save/Cancel/Reset buttons — into a host-agnostic
/// <see cref="FilterSortGroupFormHandle"/>. Extracted from <see cref="FilterSortGroupScreen"/>'s
/// constructor so the same form can be hosted either as the hand-mounted <c>_screens</c> screen (leg A)
/// or as a native Terminal.Gui <c>Dialog</c> on a nested run-loop (#554 leg B), with the caller wiring
/// only the host-specific bits: how a flash is surfaced, and how the surface closes.
/// <para>
/// All view semantics stay in the pure <see cref="FilterSortGroupForm"/>/<see cref="TaskView"/> engine;
/// this is only Terminal.Gui presentation. Per-form keys (the value field's Enter = add, the filter
/// list's Delete/Backspace = remove) live here; context command keys (Esc = Back, F1 = Help) stay on
/// the host, which knows its own back/help affordance.
/// </para>
/// </summary>
public static class FilterSortGroupFormBuilder
{
    /// <summary>
    /// Builds the form for <paramref name="current"/>. <paramref name="flash"/> surfaces an inline
    /// validation error on the host's status line; <paramref name="close"/> tears the host surface down
    /// (Save sets <see cref="FilterSortGroupFormHandle.Result"/> first, Cancel/Reset leave it null).
    /// </summary>
    public static FilterSortGroupFormHandle Build(ViewSettings current, Action<string> flash, Action close)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(flash);
        ArgumentNullException.ThrowIfNull(close);

        var handle = new FilterSortGroupFormHandle();

        // Work on a copy so Cancel leaves the caller's settings untouched.
        var working = current.Filters.Select(r => r with { }).ToList();
        var direction = current.SortDirection;

        // ── Left column: build a filter, then the active-filter list ──────────
        var addHeader = new Label { X = 1, Y = 0, Text = "─ Add a filter ─" };
        var fieldLabel = new Label { X = 1, Y = 1, Text = "Field:" };
        var fieldList = new ListView { X = 1, Y = 2, Width = 26, Height = 4 };
        fieldList.SetSource(new ObservableCollection<string>(FilterSortGroupForm.Fields.Select(TaskFieldInfo.DisplayName)));
        fieldList.SelectedItem = 0;

        var opLabel = new Label { X = 1, Y = 6, Text = "Operator:" };
        var opList = new ListView { X = 1, Y = 7, Width = 26, Height = 6 };
        opList.SetSource(new ObservableCollection<string>(FilterSortGroupForm.Ops.Select(TaskFieldInfo.OpSymbol)));
        opList.SelectedItem = 0;

        var valueLabel = new Label { X = 1, Y = 13, Text = "Value (name/me, or yyyy-mm-dd):" };
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

        // The subtasks view (hidden / mine + unassigned / all) is now owned entirely by the F4 cycle
        // (#179), superseding the old #70 "pull children" toggle that used to live here — so this form
        // no longer edits subtasks; it only preserves the current value on save.

        void AddFilter()
        {
            var field = FilterSortGroupForm.Fields[FilterSortGroupForm.Clamp(fieldList.SelectedItem, FilterSortGroupForm.Fields.Count)];
            var op = FilterSortGroupForm.Ops[FilterSortGroupForm.Clamp(opList.SelectedItem, FilterSortGroupForm.Ops.Count)];
            if (!FilterSortGroupForm.TryBuildRule(field, op, valueField.Text, out var rule, out var error))
            {
                // The hint Label that used to show this error is gone (#103) — the shared footer keeps
                // the shortcuts; surface the validation error on the host's transient status line.
                flash(error!);
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
        var clear = new Button { X = Pos.Right(cancel) + 2, Y = Pos.AnchorEnd(1), Text = "Reset to default" };

        save.Accepting += (_, _) =>
        {
            handle.Result = FilterSortGroupForm.BuildResult(
                working, sortList.SelectedItem, direction, groupList.SelectedItem, current);
            close();
        };
        cancel.Accepting += (_, _) => close();
        clear.Accepting += (_, _) =>
        {
            // Reset to the default view (the seeded "Assignee IS me" rule), not to zero filters — an
            // empty assignee rule would mean "everyone" and trigger a broad workspace fetch (#68).
            working.Clear();
            working.Add(ViewSettings.DefaultAssigneeRule());
            filterDisplay.Clear();
            foreach (var r in working)
                filterDisplay.Add(TaskFieldInfo.Describe(r));
            sortList.SelectedItem = 0;
            groupList.SelectedItem = 0;
            direction = SortDirection.Ascending;
            dirButton.Text = DirectionText(direction);
        };

        handle.Controls =
        [
            addHeader, fieldLabel, fieldList, opLabel, opList, valueLabel, valueField, addButton, removeButton,
            activeHeader, filtersList,
            sortHeader, sortLabel, sortList, dirButton, groupHeader, groupLabel, groupList,
            save, cancel, clear,
        ];
        handle.PrimaryFocus = fieldList;
        return handle;
    }

    private static string DirectionText(SortDirection direction)
        => $"Direction: {(direction == SortDirection.Ascending ? "Ascending" : "Descending")}";
}
