using System.Collections.ObjectModel;
using ClickUpTodo.ClickUp;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// See TodoApp.cs: the static `Application` API is deprecated in Terminal.Gui 2.4 but remains the
// supported v2 pattern; silence the deprecation until the instance-based API stabilizes.
#pragma warning disable CS0618

namespace ClickUpTodo.Tui.Screens;

/// <summary>
/// A full-window screen that lets the user pick a new status from a list's workflow. Enter selects
/// (and exposes the choice via <see cref="Chosen"/>); Esc cancels. The host reads <see cref="Chosen"/>
/// in its close handler.
/// </summary>
public sealed class StatusPickerScreen : Screen
{
    private readonly ListView _list;
    private readonly IReadOnlyList<StatusOption> _statuses;

    /// <summary>The chosen status name, or null if the screen was cancelled.</summary>
    public string? Chosen { get; private set; }

    public StatusPickerScreen(string taskName, IReadOnlyList<StatusOption> statuses, string? currentStatus)
    {
        _statuses = statuses;

        var title = taskName.Length > 40 ? taskName[..39] + "…" : taskName;
        Title = $"Set status — {title}";

        // The shared footer (#103) now carries the shortcuts, so the list fills the whole screen area.
        _list = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _list.SetSource(new ObservableCollection<string>(statuses.Select(StatusPickerModel.FormatStatus)));

        var preselected = StatusPickerModel.PreselectedIndex(statuses, currentStatus);
        if (preselected >= 0)
            _list.SelectedItem = preselected;

        _list.KeyDown += (_, key) =>
        {
            switch (key.KeyCode)
            {
                case KeyCode.Enter:
                    if (_list.SelectedItem is int i && i >= 0 && i < _statuses.Count)
                        Chosen = _statuses[i].Name;
                    key.Handled = true;
                    Close();
                    break;
                case KeyCode.F1:
                    key.Handled = true;
                    RequestHelp();
                    break;
                case KeyCode.Esc:
                    key.Handled = true;
                    Close();
                    break;
            }
        };

        Add([_list]);
    }

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.StatusPicker;

    public override void OnShown() => _list.SetFocus();
}
