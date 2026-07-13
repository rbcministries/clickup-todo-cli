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
/// A full-window screen (#153/#156) that lets the user change a task's <b>Status</b>, <b>Priority</b>
/// and <b>Assignees</b> without leaving their place. It hosts three vertically-stacked, focusable
/// controls; <c>Tab</c>/<c>Shift+Tab</c> cycle focus Status → Priority → Assignees (wrapping) and
/// <c>Esc</c> exits from any pane.
/// <para>
/// This is the screen <em>shell</em>: the Status pane keeps the old <c>StatusPickerScreen</c>
/// behaviour (Enter selects a status, exposed via <see cref="Chosen"/> and applied by the host), so
/// nothing regresses. The Priority and Assignees panes are present and focusable but do not yet apply
/// — Status/Priority apply-on-Enter with a <c>✓</c> current marker lands in #157, and the Assignees
/// search box + immediate apply (drawing on the frequency cache #155) lands in #158.
/// </para>
/// </summary>
public sealed class QuickUpdatesScreen : Screen
{
    private readonly ListView _statusList;
    private readonly ListView _priorityList;
    private readonly ListView _assigneesList;
    private readonly IReadOnlyList<StatusOption> _statuses;

    // The panes in focus (Tab) order — index maps to QuickUpdatesPane.
    private readonly ListView[] _panes;

    /// <summary>The chosen status name, or null if the screen was closed without picking one. The host
    /// reads this in its close handler and applies it (optimistic update, revert-on-failure).</summary>
    public string? Chosen { get; private set; }

    public QuickUpdatesScreen(
        string taskName,
        IReadOnlyList<StatusOption> statuses,
        string? currentStatus,
        int? currentPriorityLevel,
        IReadOnlyList<TaskAssignee> currentAssignees)
    {
        _statuses = statuses;

        var title = taskName.Length > 40 ? taskName[..39] + "…" : taskName;
        Title = $"Quick Updates — {title}";

        // Three bordered sections, top-to-bottom in focus order. Priority (4 fixed rows) and Assignees
        // get a compact 6-row frame each at the bottom; Status takes the remaining top space. The shared
        // footer (#103) carries the shortcuts, so no per-pane hint labels are needed.
        _statusList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _statusList.SetSource(new ObservableCollection<string>(statuses.Select(StatusPickerModel.FormatStatus)));
        var preselectedStatus = StatusPickerModel.PreselectedIndex(statuses, currentStatus);
        if (preselectedStatus >= 0)
            _statusList.SelectedItem = preselectedStatus;

        _priorityList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _priorityList.SetSource(new ObservableCollection<string>(QuickUpdatesModel.PriorityRows()));
        var preselectedPriority = QuickUpdatesModel.PreselectedPriorityIndex(currentPriorityLevel);
        if (preselectedPriority >= 0)
            _priorityList.SelectedItem = preselectedPriority;

        _assigneesList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _assigneesList.SetSource(new ObservableCollection<string>(QuickUpdatesModel.AssigneeRows(currentAssignees)));

        var statusFrame = new FrameView { Title = "Status", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(12) };
        statusFrame.Add(_statusList);

        var priorityFrame = new FrameView { Title = "Priority", X = 0, Y = Pos.AnchorEnd(12), Width = Dim.Fill(), Height = 6 };
        priorityFrame.Add(_priorityList);

        var assigneesFrame = new FrameView { Title = "Assignees", X = 0, Y = Pos.AnchorEnd(6), Width = Dim.Fill(), Height = 6 };
        assigneesFrame.Add(_assigneesList);

        _panes = [_statusList, _priorityList, _assigneesList];
        foreach (var pane in _panes)
            pane.KeyDown += OnPaneKey;

        Add([statusFrame, priorityFrame, assigneesFrame]);
    }

    public override IReadOnlyList<HelpItem> HelpItems => HelpItemSets.QuickUpdates;

    public override void OnShown() => _statusList.SetFocus();

    /// <summary>
    /// Screen-wide keys shared by all three panes. Tab/Shift+Tab cycle focus (wrapping); Esc exits;
    /// F1 opens Help; Enter on the Status pane selects a status (the other panes' apply lands in
    /// #157/#158). ↑/↓ fall through so each ListView moves its own selection.
    /// </summary>
    private void OnPaneKey(object? sender, Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.Tab:
                key.Handled = true;
                CyclePane(forward: !key.IsShift);
                break;
            case KeyCode.Enter:
                if (ReferenceEquals(sender, _statusList))
                {
                    if (_statusList.SelectedItem is int i && i >= 0 && i < _statuses.Count)
                        Chosen = _statuses[i].Name;
                    key.Handled = true;
                    Close();
                }
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
    }

    private void CyclePane(bool forward)
    {
        var current = Array.FindIndex(_panes, static p => p.HasFocus);
        if (current < 0)
            current = 0;
        var next = QuickUpdatesModel.Cycle((QuickUpdatesPane)current, forward);
        _panes[(int)next].SetFocus();
    }
}
